using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Compose;

namespace Nekostick.ServiceHost.Sync;

public sealed partial class Reconciler
{
    private async ValueTask<SyncReport> ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            return FailureReport(
                false,
                completedAt,
                null,
                "The extension data directory is unavailable.");
        }

        var settingsResult = await _settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!settingsResult.IsSuccess || settingsResult.Value is null)
        {
            return FailureReport(
                true,
                completedAt,
                settingsResult.Errors.FirstOrDefault()?.Code,
                "Unable to read extension settings.");
        }

        var consumedSettingsVersion = settingsResult.Value.Version;
        var settings = settingsResult.Value.Settings;
        if (settings is null)
        {
            return FailureReport(
                true,
                completedAt,
                ConfigurationErrorCode.Validation,
                "Extension settings have not been initialized.",
                consumedSettingsVersion);
        }

        var desiredState = await BuildDesiredStateAsync(settings, cancellationToken).ConfigureAwait(false);
        var reports = desiredState.Reports;
        if (desiredState.HasConfigFailure)
        {
            return new SyncReport(
                false,
                true,
                completedAt,
                reports.ToImmutableArray(),
                ConfigurationErrorCode.Validation,
                "One or more compose documents failed validation.")
            {
                FailureCode = SyncErrorCode.ReconcileFailed,
                ConsumedSettingsVersion = consumedSettingsVersion
            };
        }

        var desiredServices = desiredState.Desired
            .Select(item => item.Service)
            .ToImmutableArray();
        var desiredServiceIds = desiredServices.Select(service => service.Id).ToHashSet();
        var desiredRoutes = desiredState.Desired
            .Where(item => item.Route is not null)
            .Select(item => item.Route!)
            .ToImmutableArray();
        var preexistingRetirementIds = settings.Retiring
            .Select(retirement => retirement.ServiceId)
            .Where(serviceId => !desiredServiceIds.Contains(serviceId))
            .ToHashSet();

        var writtenConfigurationVersion = (long?)null;
        var committedSettingsVersion = (long?)null;
        var orphanNotes = ImmutableArray<string>.Empty;
        HashSet<Guid> stageBEligible = new();
        SvchostSettings? stageASettings = null;
        ConfigurationChangeSet? committedChanges = null;
        var stageAWriteCommitted = false;
        HostConfigurationSnapshot? stageASnapshot = null;

        for (var attempt = 0; attempt < MaxReplaceAttempts; attempt++)
        {
            var snapshotResult = await _fullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshotResult.IsSuccess || snapshotResult.Value is null)
            {
                return FailureReport(
                    true,
                    completedAt,
                    snapshotResult.Errors.FirstOrDefault()?.Code,
                    "Unable to read the full Host configuration.",
                    consumedSettingsVersion,
                    writtenConfigurationVersion,
                    reports.ToImmutableArray(),
                    orphanNotes,
                    committedSettingsVersion);
            }

            var snapshot = snapshotResult.Value;
            var ownSettings = FindOwnSettingsRow(snapshot);
            if (ownSettings is null || ownSettings.Version != consumedSettingsVersion)
            {
                return SettingsConflictReport(
                    completedAt,
                    consumedSettingsVersion,
                    writtenConfigurationVersion,
                    reports.ToImmutableArray(),
                    orphanNotes,
                    committedSettingsVersion);
            }

            var snapshotServiceIds = snapshot.Services.Select(service => service.Id).ToHashSet();
            var preservedSourceServiceIds = desiredState.SourceFailureServiceIds
                .Where(snapshotServiceIds.Contains)
                .ToHashSet();
            var preservedSourceRouteIds = snapshot.Routes
                .Where(route =>
                    desiredState.SourceFailureRouteIds.Contains(route.Id) &&
                    route.Target is MicroserviceRouteTargetConfiguration microservice &&
                    preservedSourceServiceIds.Contains(microservice.ServiceId) &&
                    IsOwnedRoute(route.MetadataJson))
                .Select(route => route.Id)
                .ToHashSet();

            for (var index = 0; index < reports.Count; index++)
            {
                var report = reports[index];
                if (report.ServiceId is { } serviceId &&
                    desiredState.SourceFailureServiceIds.Contains(serviceId))
                {
                    var isPreserved = preservedSourceServiceIds.Contains(serviceId);
                    reports[index] = report with
                    {
                        NodeLocal = report.FailureCode == SyncErrorCode.SourceFailed && isPreserved,
                        Decision = isPreserved ? ServiceDecision.Preserved : ServiceDecision.Failed
                    };
                }
            }

            var orphanSweep = FindOrphanSweep(
                snapshot,
                desiredState.ConfiguredLockServiceIds,
                desiredServiceIds);
            orphanNotes = orphanSweep.Notes;

            var retirementsToAdd = desiredState.RetirementsToAdd
                .Select(CloneRetirement)
                .ToList();
            foreach (var orphanServiceId in orphanSweep.ServiceIds.OrderBy(id => id))
            {
                var orphanService = snapshot.Services.FirstOrDefault(service => service.Id == orphanServiceId);
                if (orphanService is null)
                {
                    continue;
                }

                var ownedRoutes = snapshot.Routes
                    .Where(route =>
                        route.Target is MicroserviceRouteTargetConfiguration microservice &&
                        microservice.ServiceId == orphanServiceId &&
                        IsOwnedRoute(route.MetadataJson))
                    .OrderBy(route => route.Id)
                    .ToArray();
                var metadataRoute = ownedRoutes.FirstOrDefault(route =>
                    TryGetOwnedRouteMetadata(route.MetadataJson, out _, out _));
                if (metadataRoute is null ||
                    !TryGetOwnedRouteMetadata(metadataRoute.MetadataJson, out var metadataConfigName, out var metadataServiceName))
                {
                    continue;
                }

                var configName = IsValidRetirementConfigName(metadataConfigName)
                    ? metadataConfigName
                    : "orphan";
                var serviceName = IsValidServiceName(metadataServiceName)
                    ? metadataServiceName
                    : "orphan";
                var scope = ResolveOrphanScope(settings, metadataConfigName, orphanService.FileName);
                SvchostSettings.AddRetirement(
                    retirementsToAdd,
                    new RetiringServiceSettings(
                        orphanServiceId,
                        ownedRoutes.Select(route => route.Id),
                        configName,
                        serviceName,
                        scope == ComposeServiceScope.Document ? "document" : "global"));
            }

            stageBEligible = GetStageBEligible(snapshot, preexistingRetirementIds);
            stageASettings = BuildStageASettings(
                settings,
                desiredState,
                retirementsToAdd,
                desiredServiceIds,
                snapshotServiceIds);
            var retiringServiceIds = stageASettings.Retiring
                .Select(retirement => retirement.ServiceId)
                .Where(serviceId => !desiredServiceIds.Contains(serviceId))
                .ToHashSet();
            var settingsRow = SettingsDocumentsEqual(settings, stageASettings)
                ? ownSettings
                : _settingsStore.CreateExtensionSettingsConfiguration(
                    stageASettings,
                    ownSettings.Version);
            var changes = BuildChangeSet(
                snapshot,
                desiredState.ManagedRouteIds,
                desiredServices,
                desiredRoutes,
                retiringServiceIds,
                preservedSourceServiceIds,
                preservedSourceRouteIds,
                settingsRow);

            var serviceDiffs = new Dictionary<Guid, ImmutableArray<ServiceFieldDiff>>();
            foreach (var desired in desiredServices)
            {
                _ = PreserveServiceVersion(desired, snapshot, out var diffs);
                if (!diffs.IsEmpty)
                {
                    serviceDiffs[desired.Id] = diffs;
                }
            }

            for (var index = 0; index < reports.Count; index++)
            {
                var report = reports[index];
                if (report.Succeeded && report.ServiceId is { } serviceId)
                {
                    var diffs = serviceDiffs.TryGetValue(serviceId, out var serviceFieldDiffs)
                        ? serviceFieldDiffs
                        : ImmutableArray<ServiceFieldDiff>.Empty;
                    reports[index] = report with
                    {
                        Diffs = diffs,
                        Decision = !snapshotServiceIds.Contains(serviceId) || !diffs.IsEmpty
                            ? ServiceDecision.Updated
                            : ServiceDecision.Reused
                    };
                }
            }

            var blockingRoutes = changes.Routes
                .Where(route =>
                    route.Target is MicroserviceRouteTargetConfiguration microservice &&
                    retiringServiceIds.Contains(microservice.ServiceId))
                .Select(route => route.Id)
                .ToArray();
            if (blockingRoutes.Length > 0)
            {
                var blockedServiceIds = blockingRoutes
                    .Select(routeId => snapshot.Routes.First(route => route.Id == routeId).Target)
                    .OfType<MicroserviceRouteTargetConfiguration>()
                    .Select(target => target.ServiceId)
                    .Distinct()
                    .OrderBy(id => id);
                return FailureReport(
                    true,
                    completedAt,
                    ConfigurationErrorCode.Validation,
                    $"Unable to remove service(s) {string.Join(", ", blockedServiceIds)}: route(s) {string.Join(", ", blockingRoutes.OrderBy(id => id))} still target them and are not managed by svchost.",
                    consumedSettingsVersion,
                    writtenConfigurationVersion,
                    reports.ToImmutableArray(),
                    orphanNotes,
                    committedSettingsVersion);
            }

            if (ConfigurationEquals(snapshot, changes))
            {
                stageASnapshot = snapshot;
                committedChanges = changes;
                break;
            }

            var replaceResult = await _fullConfiguration.ReplaceAsync(
                    snapshot.Version,
                    changes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replaceResult.NewVersion is { } newVersion)
            {
                writtenConfigurationVersion = newVersion;
            }

            if (replaceResult.IsSuccess)
            {
                stageASnapshot = snapshot;
                committedChanges = changes;
                committedSettingsVersion = await ReadCommittedSettingsVersionAsync(
                        stageASettings,
                        cancellationToken)
                    .ConfigureAwait(false);
                stageAWriteCommitted = true;

                break;
            }

            if (!HasError(replaceResult.Errors, ConfigurationErrorCode.ConcurrencyConflict) ||
                attempt + 1 >= MaxReplaceAttempts)
            {
                return FailureReport(
                    true,
                    completedAt,
                    replaceResult.Errors.FirstOrDefault()?.Code,
                    ReplaceFailureMessage(replaceResult.Errors),
                    consumedSettingsVersion,
                    writtenConfigurationVersion,
                    reports.ToImmutableArray(),
                    orphanNotes,
                    committedSettingsVersion);
            }
        }

        if (stageASnapshot is null || committedChanges is null || stageASettings is null)
        {
            return FailureReport(
                true,
                completedAt,
                ConfigurationErrorCode.ConcurrencyConflict,
                "Unable to replace the full Host configuration.",
                consumedSettingsVersion,
                writtenConfigurationVersion,
                reports.ToImmutableArray(),
                orphanNotes,
                committedSettingsVersion);
        }

        if (stageAWriteCommitted)
        {
            await CleanupCommittedStateBestEffortAsync(
                    stageASettings,
                    committedChanges.Services,
                    stageASettings.Retiring,
                    Array.Empty<RetiringServiceSettings>(),
                    allowConfigDirectoryCleanup: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var finalSettings = stageASettings;
        var finalServices = committedChanges.Services;
        var removedRetirements = new List<RetiringServiceSettings>();
        var stageBValidationFailed = false;
        if (stageBEligible.Count > 0)
        {
            for (var attempt = 0; attempt < MaxReplaceAttempts; attempt++)
            {
                var snapshotResult = await _fullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!snapshotResult.IsSuccess || snapshotResult.Value is null)
                {
                    return FailureReport(
                        true,
                        completedAt,
                        snapshotResult.Errors.FirstOrDefault()?.Code,
                        "Unable to read the full Host configuration for pending removals.",
                        consumedSettingsVersion,
                        writtenConfigurationVersion,
                        reports.ToImmutableArray(),
                        orphanNotes,
                        committedSettingsVersion);
                }

                var snapshot = snapshotResult.Value;
                var settingsRead = await _settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (!settingsRead.IsSuccess || settingsRead.Value?.Settings is null)
                {
                    return FailureReport(
                        true,
                        completedAt,
                        settingsRead.Errors.FirstOrDefault()?.Code ?? ConfigurationErrorCode.Validation,
                        "Unable to read extension settings for pending removals.",
                        consumedSettingsVersion,
                        writtenConfigurationVersion,
                        reports.ToImmutableArray(),
                        orphanNotes,
                        committedSettingsVersion);
                }

                var currentSettings = settingsRead.Value.Settings;
                var ownSettings = FindOwnSettingsRow(snapshot);
                if (ownSettings is null || ownSettings.Version != settingsRead.Value.Version ||
                    !SettingsDocumentsEqual(currentSettings, stageASettings))
                {
                    return SettingsConflictReport(
                        completedAt,
                        consumedSettingsVersion,
                        writtenConfigurationVersion,
                        reports.ToImmutableArray(),
                        orphanNotes,
                        committedSettingsVersion);
                }

                var removableIds = GetStageBEligible(snapshot, stageBEligible);
                var recordedRetirementIds = currentSettings.Retiring
                    .Select(retirement => retirement.ServiceId)
                    .ToHashSet();
                removableIds.IntersectWith(recordedRetirementIds);
                if (removableIds.Count == 0)
                {
                    break;
                }

                var removedEntries = currentSettings.Retiring
                    .Where(retirement => removableIds.Contains(retirement.ServiceId))
                    .Select(CloneRetirement)
                    .ToList();
                var nextSettings = CloneSettings(currentSettings);
                nextSettings.Retiring.RemoveAll(retirement => removableIds.Contains(retirement.ServiceId));
                var nextSettingsRow = SettingsDocumentsEqual(currentSettings, nextSettings)
                    ? ownSettings
                    : _settingsStore.CreateExtensionSettingsConfiguration(
                        nextSettings,
                        ownSettings.Version);
                var stageBChanges = new ConfigurationChangeSet(
                    snapshot.GlobalSettings,
                    snapshot.Routes,
                    snapshot.Services
                        .Where(service => !removableIds.Contains(service.Id))
                        .ToImmutableArray(),
                    snapshot.ExtensionRecords,
                    ReplaceOwnSettingsRow(snapshot, nextSettingsRow));

                var replaceResult = await _fullConfiguration.ReplaceAsync(
                        snapshot.Version,
                        stageBChanges,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (replaceResult.NewVersion is { } newVersion)
                {
                    writtenConfigurationVersion = newVersion;
                }

                if (replaceResult.IsSuccess)
                {
                    removedRetirements = removedEntries;
                    finalSettings = nextSettings;
                    finalServices = stageBChanges.Services;
                    committedSettingsVersion = await ReadCommittedSettingsVersionAsync(
                            nextSettings,
                            cancellationToken)
                        .ConfigureAwait(false);
                    orphanNotes = orphanNotes.AddRange(removedEntries.Select(entry =>
                        $"Removed retiring service {entry.ServiceId} from the Host configuration."));
                    await CleanupCommittedStateBestEffortAsync(
                            finalSettings,
                            finalServices,
                            stageASettings.Retiring,
                            removedRetirements,
                            allowConfigDirectoryCleanup: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                if (HasError(replaceResult.Errors, ConfigurationErrorCode.ConcurrencyConflict) &&
                    attempt + 1 < MaxReplaceAttempts)
                {
                    continue;
                }

                if (HasError(replaceResult.Errors, ConfigurationErrorCode.Validation))
                {
                    stageBValidationFailed = true;
                    break;
                }

                return FailureReport(
                    true,
                    completedAt,
                    replaceResult.Errors.FirstOrDefault()?.Code,
                    ReplaceFailureMessage(replaceResult.Errors),
                    consumedSettingsVersion,
                    writtenConfigurationVersion,
                    reports.ToImmutableArray(),
                    orphanNotes,
                    committedSettingsVersion);
            }
        }

        var pendingRetirements = finalSettings.Retiring;
        foreach (var retirement in pendingRetirements)
        {
            AddRemovalPendingReport(reports, retirement, stageASnapshot);
        }

        var succeeded = pendingRetirements.Count == 0 && reports.All(report => report.Succeeded);
        if (pendingRetirements.Count > 0)
        {
            var pendingIds = pendingRetirements
                .Select(retirement => retirement.ServiceId)
                .OrderBy(id => id)
                .ToArray();
            orphanNotes = orphanNotes.Add(
                $"Removal pending for service ID(s): {string.Join(", ", pendingIds)}.");
        }

        var managedServiceIds = desiredState.ConfiguredLockServiceIds
            .Concat(desiredState.UpdatedLocks.Values.Select(lockEntry => lockEntry.ServiceId))
            .ToHashSet();
        var finalRetirementIds = pendingRetirements
            .Select(retirement => retirement.ServiceId)
            .ToHashSet();
        await ResumeWaitingServicesBestEffortAsync(
                managedServiceIds,
                finalServices,
                finalRetirementIds,
                cancellationToken)
            .ConfigureAwait(false);

        return new SyncReport(
            succeeded,
            true,
            DateTimeOffset.UtcNow,
            reports.ToImmutableArray(),
            pendingRetirements.Count > 0 && stageBValidationFailed
                ? ConfigurationErrorCode.Validation
                : null,
            pendingRetirements.Count > 0 ? "One or more service removals are pending." : null)
        {
            FailureCode = pendingRetirements.Count > 0 ? SyncErrorCode.RemovalPending : null,
            ConsumedSettingsVersion = consumedSettingsVersion,
            CommittedSettingsVersion = committedSettingsVersion,
            Notes = orphanNotes,
            WrittenConfigurationVersion = writtenConfigurationVersion
        };
    }

    private SvchostSettings BuildStageASettings(
        SvchostSettings settings,
        DesiredStatePlan desiredState,
        IEnumerable<RetiringServiceSettings> retirementsToAdd,
        IReadOnlySet<Guid> desiredServiceIds,
        IReadOnlySet<Guid> snapshotServiceIds)
    {
        var updated = CloneSettings(settings);
        foreach (var pair in desiredState.ConfigServiceNames)
        {
            if (!updated.Configs.TryGetValue(pair.Key, out var config) || config is null)
            {
                continue;
            }

            foreach (var serviceName in config.Lock.Services.Keys.ToArray())
            {
                if (!pair.Value.Contains(serviceName))
                {
                    config.Lock.Services.Remove(serviceName);
                }
            }

            foreach (var lockPair in desiredState.UpdatedLocks)
            {
                if (!string.Equals(lockPair.Key.ConfigName, pair.Key, StringComparison.Ordinal))
                {
                    continue;
                }

                config.Lock.Services.TryGetValue(lockPair.Key.ServiceName, out var existingLock);
                config.Lock.Services[lockPair.Key.ServiceName] = new LockServiceEntry(
                    SourceResolver.PreserveUnchangedSourceLock(existingLock?.Source, lockPair.Value.Source),
                    lockPair.Value.ServiceId,
                    lockPair.Value.RouteIds);
            }

            config.Stopped = config.Stopped
                .Where(pair.Value.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        updated.Retiring.RemoveAll(retirement =>
            desiredServiceIds.Contains(retirement.ServiceId) ||
            !snapshotServiceIds.Contains(retirement.ServiceId));
        foreach (var retirement in retirementsToAdd)
        {
            if (!desiredServiceIds.Contains(retirement.ServiceId) &&
                snapshotServiceIds.Contains(retirement.ServiceId))
            {
                SvchostSettings.AddRetirement(updated.Retiring, CloneRetirement(retirement));
            }
        }

        return updated;
    }

    private static SvchostSettings CloneSettings(SvchostSettings settings)
    {
        var configs = new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal);
        foreach (var pair in settings.Configs)
        {
            var current = pair.Value;
            var lockServices = current.Lock?.Services is { } services
                ? new Dictionary<string, LockServiceEntry>(services, StringComparer.Ordinal)
                : new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal);
            configs.Add(
                pair.Key,
                new SvchostConfigSettings(
                    current.Yaml,
                    new LockModel { Services = lockServices },
                    current.Stopped));
        }

        return new SvchostSettings(
            settings.ApiKey,
            settings.Routes,
            configs,
            settings.ReleaseProviders,
            settings.Retiring.Select(CloneRetirement))
        {
            Observability = settings.Observability
        };
    }

    private static RetiringServiceSettings CloneRetirement(RetiringServiceSettings retirement) =>
        new(
            retirement.ServiceId,
            retirement.RouteIds ?? new List<Guid>(),
            retirement.ConfigName,
            retirement.ServiceName,
            retirement.Scope);

    private ComposeServiceScope ResolveOrphanScope(
        SvchostSettings settings,
        string configName,
        string fileName)
    {
        if (settings.Configs.TryGetValue(configName, out var config) && config is not null)
        {
            try
            {
                return _composeFileParser.Parse(config.Yaml ?? string.Empty).ServiceScope;
            }
            catch (ComposeValidationException)
            {
            }
        }

        try
        {
            var filePath = Path.GetFullPath(fileName);
            var globalRoot = Path.GetFullPath(Path.Combine(_dataDirectory, "svchost", "global"));
            return IsPathWithin(globalRoot, filePath)
                ? ComposeServiceScope.Global
                : ComposeServiceScope.Document;
        }
        catch (ArgumentException)
        {
            return ComposeServiceScope.Global;
        }
    }

    private static bool IsValidRetirementConfigName(string? name) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            name ?? string.Empty,
            SvchostSettingsSchema.NamePattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
        !string.Equals(name, "global", StringComparison.OrdinalIgnoreCase);

    private static bool IsPathWithin(string parentDirectory, string candidate)
    {
        var parent = Path.GetFullPath(parentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return path.StartsWith(parent + Path.DirectorySeparatorChar, comparison) ||
               path.StartsWith(parent + Path.AltDirectorySeparatorChar, comparison);
    }

    private static HashSet<Guid> GetStageBEligible(
        HostConfigurationSnapshot snapshot,
        IEnumerable<Guid> retirementIds)
    {
        var retiring = retirementIds.ToHashSet();
        var routeTargets = snapshot.Routes
            .Select(route => route.Target)
            .OfType<MicroserviceRouteTargetConfiguration>()
            .Select(target => target.ServiceId)
            .ToHashSet();
        return snapshot.Services
            .Where(service =>
                retiring.Contains(service.Id) &&
                !service.Enabled &&
                !routeTargets.Contains(service.Id))
            .Select(service => service.Id)
            .ToHashSet();
    }

    private static bool ConfigurationEquals(
        HostConfigurationSnapshot snapshot,
        ConfigurationChangeSet changes) =>
        SemanticallyEqualIgnoringVersion(snapshot.Services, changes.Services) &&
        SemanticallyEqualIgnoringVersion(snapshot.Routes, changes.Routes) &&
        SemanticallyEqualIgnoringVersion(snapshot.ExtensionSettings, changes.ExtensionSettings);

    private static ExtensionSettingsConfiguration? FindOwnSettingsRow(HostConfigurationSnapshot snapshot)
    {
        ExtensionSettingsConfiguration? ownSettings = null;
        foreach (var entry in snapshot.ExtensionSettings)
        {
            if (!string.Equals(entry.ExtensionId, Owner, StringComparison.Ordinal))
            {
                continue;
            }

            if (ownSettings is not null)
            {
                return null;
            }

            ownSettings = entry;
        }

        return ownSettings;
    }

    private static ImmutableArray<ExtensionSettingsConfiguration> ReplaceOwnSettingsRow(
        HostConfigurationSnapshot snapshot,
        ExtensionSettingsConfiguration updatedSettings)
    {
        var settings = ImmutableArray.CreateBuilder<ExtensionSettingsConfiguration>(
            snapshot.ExtensionSettings.Length);
        var found = false;
        foreach (var entry in snapshot.ExtensionSettings)
        {
            if (string.Equals(entry.ExtensionId, Owner, StringComparison.Ordinal))
            {
                if (found)
                {
                    throw new InvalidOperationException("The full Host snapshot contains duplicate svchost settings rows.");
                }

                settings.Add(updatedSettings);
                found = true;
            }
            else
            {
                settings.Add(entry);
            }
        }

        if (!found)
        {
            throw new InvalidOperationException("The svchost settings row is absent from the full Host snapshot.");
        }

        return settings.ToImmutable();
    }

    private bool SettingsDocumentsEqual(SvchostSettings left, SvchostSettings right) =>
        JsonDocumentsEqual(
            _settingsStore.SerializeSettings(left),
            _settingsStore.SerializeSettings(right));

    private static bool JsonDocumentsEqual(string left, string right)
    {
        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
        }
        catch (JsonException)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private async ValueTask<long?> ReadCommittedSettingsVersionAsync(
        SvchostSettings expectedSettings,
        CancellationToken cancellationToken)
    {
        var read = await _settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return read.IsSuccess &&
               read.Value?.Settings is { } current &&
               SettingsDocumentsEqual(current, expectedSettings)
            ? read.Value.Version
            : null;
    }

    private static void AddRemovalPendingReport(
        List<ServiceSyncReport> reports,
        RetiringServiceSettings retirement,
        HostConfigurationSnapshot snapshot)
    {
        var serviceChanged = snapshot.Services.Any(service =>
            service.Id == retirement.ServiceId && service.Enabled);
        var routesChanged = snapshot.Routes.Any(route =>
            route.Target is MicroserviceRouteTargetConfiguration microservice &&
            microservice.ServiceId == retirement.ServiceId &&
            IsOwnedRoute(route.MetadataJson));
        var reportIndex = reports.FindIndex(report => report.ServiceId == retirement.ServiceId);
        if (reportIndex >= 0)
        {
            reports[reportIndex] = reports[reportIndex] with
            {
                Succeeded = false,
                Changed = reports[reportIndex].Changed || serviceChanged || routesChanged,
                Error = "Service removal is pending.",
                FailureCode = SyncErrorCode.RemovalPending,
                NodeLocal = false,
                Decision = ServiceDecision.RemovalPending
            };
            return;
        }

        reports.Add(new ServiceSyncReport(
            retirement.ConfigName,
            retirement.ServiceName,
            false,
            serviceChanged || routesChanged,
            retirement.ServiceId,
            retirement.RouteIds.ToImmutableArray(),
            "Service removal is pending.",
            SyncErrorCode.RemovalPending)
        {
            Decision = ServiceDecision.RemovalPending
        });
    }

    private async ValueTask ResumeWaitingServicesBestEffortAsync(
        IReadOnlySet<Guid> managedServiceIds,
        ImmutableArray<ServiceConfiguration> services,
        IReadOnlySet<Guid> retiringServiceIds,
        CancellationToken cancellationToken)
    {
        if (_supervisor is null)
        {
            return;
        }

        var desiredFileNames = services
            .Where(service =>
                managedServiceIds.Contains(service.Id) &&
                !retiringServiceIds.Contains(service.Id))
            .ToDictionary(service => service.Id, service => service.FileName);
        if (desiredFileNames.Count == 0)
        {
            return;
        }

        try
        {
            var telemetry = await _supervisor.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!telemetry.IsSuccess)
            {
                Debug.WriteLine($"{Owner}: unable to read supervisor telemetry before resume.");
                return;
            }

            foreach (var runtime in telemetry.Value
                         .Where(snapshot =>
                             snapshot.LifecycleState == ExtensionServiceLifecycleState.Waiting &&
                             desiredFileNames.TryGetValue(snapshot.ServiceId, out var fileName) &&
                             File.Exists(fileName))
                         .OrderBy(snapshot => snapshot.ServiceId))
            {
                try
                {
                    var result = await _supervisor.ResumeAsync(runtime.ServiceId, cancellationToken)
                        .ConfigureAwait(false);
                    var outcome = result.IsSuccess
                        ? result.IsNoOp ? "NoOp" : "Success"
                        : result.Errors.FirstOrDefault()?.Code.ToString() ?? "Error";
                    Debug.WriteLine($"{Owner}: resumed waiting service {runtime.ServiceId}: {outcome}.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Debug.WriteLine($"{Owner}: resume timed out for waiting service {runtime.ServiceId}.");
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"{Owner}: resume failed for waiting service {runtime.ServiceId}: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine($"{Owner}: supervisor telemetry read timed out before resume.");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"{Owner}: supervisor telemetry read failed before resume: {exception.Message}");
        }
    }

    private static bool HasError(IEnumerable<ConfigurationError> errors, ConfigurationErrorCode code) =>
        errors.Any(error => error.Code == code);

    private static SyncReport SettingsConflictReport(
        DateTimeOffset completedAt,
        long consumedSettingsVersion,
        long? writtenConfigurationVersion,
        ImmutableArray<ServiceSyncReport> reports,
        ImmutableArray<string> notes,
        long? committedSettingsVersion) =>
        FailureReport(
            true,
            completedAt,
            ConfigurationErrorCode.ConcurrencyConflict,
            "Extension settings changed during reconciliation; retry synchronization.",
            consumedSettingsVersion,
            writtenConfigurationVersion,
            reports,
            notes,
            committedSettingsVersion);

    private static SyncReport FailureReport(
        bool dataDirectoryAvailable,
        DateTimeOffset completedAt,
        ConfigurationErrorCode? code,
        string message,
        long? consumedSettingsVersion = null,
        long? writtenConfigurationVersion = null,
        ImmutableArray<ServiceSyncReport> services = default,
        ImmutableArray<string> notes = default,
        long? committedSettingsVersion = null) =>
        new(
            false,
            dataDirectoryAvailable,
            completedAt,
            services.IsDefault ? ImmutableArray<ServiceSyncReport>.Empty : services,
            code,
            message)
        {
            FailureCode = SyncErrorCode.ReconcileFailed,
            ConsumedSettingsVersion = consumedSettingsVersion,
            CommittedSettingsVersion = committedSettingsVersion,
            Notes = notes.IsDefault ? ImmutableArray<string>.Empty : notes,
            WrittenConfigurationVersion = writtenConfigurationVersion
        };

    private static string ReplaceFailureMessage(ImmutableArray<ConfigurationError> errors)
    {
        var details = errors.IsDefaultOrEmpty
            ? null
            : string.Join(
                "; ",
                errors
                    .Select(error => string.IsNullOrWhiteSpace(error.Message)
                        ? error.Code.ToString()
                        : $"{error.Code}: {error.Message}")
                    .Distinct(StringComparer.Ordinal));
        return string.IsNullOrEmpty(details)
            ? "Unable to replace the full Host configuration."
            : $"Unable to replace the full Host configuration ({details}).";
    }
}
