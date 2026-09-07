using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.ServiceHost.Settings;

namespace Nekolla.Nekostick.ServiceHost.Sync;

public sealed partial class Reconciler
{
    private static bool LockStateWouldChange(
        SvchostSettings settings,
        IReadOnlyDictionary<string, ImmutableHashSet<string>> configServiceNames,
        IReadOnlyDictionary<string, string> configYamls,
        IReadOnlyDictionary<(string ConfigName, string ServiceName), LockServiceEntry> updatedLocks)
    {
        foreach (var configPair in configServiceNames)
        {
            if (!settings.Configs.TryGetValue(configPair.Key, out var config) || config is null)
            {
                return true;
            }

            var locks = config.Lock?.Services ?? new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal);
            if (configYamls.TryGetValue(configPair.Key, out var expectedYaml) &&
                string.Equals(config.Yaml, expectedYaml, StringComparison.Ordinal))
            {
                locks = locks
                    .Where(pair => configPair.Value.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            }

            foreach (var updated in updatedLocks.Where(pair =>
                         string.Equals(pair.Key.ConfigName, configPair.Key, StringComparison.Ordinal)))
            {
                locks[updated.Key.ServiceName] = updated.Value;
            }

            var currentLocks = config.Lock?.Services ?? new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal);
            if (!LockDictionariesEqual(currentLocks, locks))
            {
                return true;
            }

            var expectedStopped = (config.Stopped ?? Array.Empty<string>())
                .Where(configPair.Value.Contains)
                .Distinct(StringComparer.Ordinal);
            if (!StringSetsEqual(config.Stopped ?? Array.Empty<string>(), expectedStopped))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LockDictionariesEqual(
        IReadOnlyDictionary<string, LockServiceEntry> left,
        IReadOnlyDictionary<string, LockServiceEntry> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        return left.All(pair =>
            right.TryGetValue(pair.Key, out var value) && LockEntriesEqual(pair.Value, value));
    }

    private static bool LockEntriesEqual(LockServiceEntry? left, LockServiceEntry? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return SourceEquals(left.Source, right.Source) &&
            left.ServiceId == right.ServiceId &&
            (left.RouteIds ?? new List<Guid>()).SequenceEqual(right.RouteIds ?? new List<Guid>());
    }

    private static bool SourceEquals(LockSource? left, LockSource? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
            string.Equals(left.Url, right.Url, StringComparison.Ordinal) &&
            string.Equals(left.Path, right.Path, StringComparison.Ordinal) &&
            string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal) &&
            left.Size == right.Size &&
            left.FetchedAt == right.FetchedAt;
    }

    private static bool StringSetsEqual(IEnumerable<string> left, IEnumerable<string> right) =>
        left.ToHashSet(StringComparer.Ordinal).SetEquals(right);

    private async ValueTask<SyncReport?> PersistLockStateAsync(
        SvchostSettings settings,
        IReadOnlyDictionary<string, ImmutableHashSet<string>> configServiceNames,
        IReadOnlyDictionary<string, string> configYamls,
        IReadOnlyDictionary<(string ConfigName, string ServiceName), LockServiceEntry> updatedLocks,
        List<ServiceSyncReport> reports,
        DateTimeOffset completedAt,
        long consumedSettingsVersion,
        CancellationToken cancellationToken)
    {
        if (!LockStateWouldChange(settings, configServiceNames, configYamls, updatedLocks))
        {
            return null;
        }

        var lockWrite = await _settingsStore.UpdateSettingsAsync(
                latestSettings =>
                {
                    foreach (var configPair in configServiceNames)
                    {
                        if (!latestSettings.Configs.TryGetValue(configPair.Key, out var latestConfig) || latestConfig is null)
                        {
                            continue;
                        }

                        latestConfig.Lock ??= new LockModel();
                        var locks = latestConfig.Lock.Services ?? new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal);
                        if (configYamls.TryGetValue(configPair.Key, out var expectedYaml) &&
                            string.Equals(latestConfig.Yaml, expectedYaml, StringComparison.Ordinal))
                        {
                            locks = locks
                                .Where(pair => configPair.Value.Contains(pair.Key))
                                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                        }

                        foreach (var updated in updatedLocks.Where(pair =>
                                     string.Equals(pair.Key.ConfigName, configPair.Key, StringComparison.Ordinal)))
                        {
                            locks[updated.Key.ServiceName] = updated.Value;
                        }

                        latestConfig.Stopped = (latestConfig.Stopped ?? Array.Empty<string>())
                            .Where(configPair.Value.Contains)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();

                        latestConfig.Lock.Services = locks;
                    }

                    return latestSettings;
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (lockWrite.IsSuccess)
        {
            return null;
        }

        await ResetAffectedLockSourcesBestEffortAsync(updatedLocks.Keys, cancellationToken)
            .ConfigureAwait(false);
        return new SyncReport(
            false,
            true,
            completedAt,
            reports.ToImmutableArray(),
            lockWrite.Errors.FirstOrDefault()?.Code,
            "Host configuration was replaced but the source lock could not be persisted. " +
            "Retry synchronization to restore lock state; affected sources will be treated as new.")
        {
            // Resetting Source forfeits drift detection for the replaced generation, but avoids
            // claiming that an unpersisted lock still describes the replaced artifact.
            FailureCode = SyncErrorCode.LockPersistFailed,
            ConsumedSettingsVersion = consumedSettingsVersion
        };
    }

    private async ValueTask ResetAffectedLockSourcesBestEffortAsync(
        IEnumerable<(string ConfigName, string ServiceName)> affected,
        CancellationToken cancellationToken)
    {
        var affectedSet = affected.ToHashSet();
        if (affectedSet.Count == 0)
        {
            return;
        }

        try
        {
            await _settingsStore.UpdateSettingsAsync(
                    latestSettings =>
                    {
                        foreach (var key in affectedSet)
                        {
                            if (!latestSettings.Configs.TryGetValue(key.ConfigName, out var config) || config is null)
                            {
                                continue;
                            }

                            var locks = config.Lock?.Services;
                            if (locks is null || !locks.TryGetValue(key.ServiceName, out var entry) || entry is null)
                            {
                                continue;
                            }

                            // This deliberately forfeits drift detection for the replaced generation.
                            entry.Source = null!;
                        }

                        return latestSettings;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Recovery is best effort; the primary report retains the lock persistence failure.
        }
    }
}
