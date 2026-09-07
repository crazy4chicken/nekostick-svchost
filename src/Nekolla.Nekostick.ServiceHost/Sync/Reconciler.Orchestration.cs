using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.ServiceHost.Sync;

public sealed partial class Reconciler
{
    private async ValueTask<SyncReport> ReconcileCoreAsync(
        IReadOnlySet<Guid> extraManagedServiceIds,
        IReadOnlySet<Guid> extraManagedRouteIds,
        CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            return new SyncReport(
                false,
                false,
                completedAt,
                ImmutableArray<ServiceSyncReport>.Empty,
                null,
                "The extension data directory is unavailable.")
            {
                FailureCode = SyncErrorCode.ReconcileFailed
            };
        }

        var settingsResult = await _settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!settingsResult.IsSuccess)
        {
            return FailureReport(
                true,
                completedAt,
                settingsResult.Errors.FirstOrDefault()?.Code,
                "Unable to read extension settings.");
        }

        var consumedSettingsVersion = settingsResult.Value!.Version;
        var settings = settingsResult.Value!.Settings;
        if (settings is null)
        {
            return FailureReport(
                true,
                completedAt,
                ConfigurationErrorCode.Validation,
                "Extension settings have not been initialized.",
                consumedSettingsVersion);
        }

        var desiredState = await BuildDesiredStateAsync(
                settings,
                extraManagedServiceIds,
                extraManagedRouteIds,
                cancellationToken)
            .ConfigureAwait(false);
        var reports = desiredState.Reports;
        if (desiredState.ParseFailed)
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
        var desiredRoutes = desiredState.Desired
            .Where(item => item.Route is not null)
            .Select(item => item.Route!)
            .ToImmutableArray();
        var refreshedServiceIds = desiredState.Desired
            .Where(item => !item.SourceReused)
            .Select(item => item.Service.Id)
            .ToHashSet();
        var sourceChanged = refreshedServiceIds.Count > 0;
        var replaceCompleted = false;
        for (var attempt = 0; attempt < MaxReplaceAttempts; attempt++)
        {
            var snapshotResult = await _fullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshotResult.IsSuccess || snapshotResult.Value is null)
            {
                return new SyncReport(
                    false,
                    true,
                    completedAt,
                    reports.ToImmutableArray(),
                    snapshotResult.Errors.FirstOrDefault()?.Code,
                    "Unable to read the full Host configuration.")
                {
                    FailureCode = SyncErrorCode.ReconcileFailed,
                    ConsumedSettingsVersion = consumedSettingsVersion
                };
            }

            var snapshot = snapshotResult.Value;
            var changes = BuildChangeSet(
                snapshot,
                desiredState.ManagedServiceIds,
                desiredState.ManagedRouteIds,
                desiredServices.Select(service => PreserveServiceVersion(
                    service,
                    snapshot,
                    refreshedServiceIds.Contains(service.Id))),
                desiredRoutes.Select(route => PreserveRouteVersion(route, snapshot)));
            if (!sourceChanged &&
                SemanticallyEqualIgnoringVersion(snapshot.Services, changes.Services) &&
                SemanticallyEqualIgnoringVersion(snapshot.Routes, changes.Routes))
            {
                replaceCompleted = true;
                break;
            }

            var replaceResult = await _fullConfiguration.ReplaceAsync(
                    snapshot.Version,
                    changes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replaceResult.IsSuccess)
            {
                replaceCompleted = true;
                break;
            }

            if (!HasError(replaceResult.Errors, ConfigurationErrorCode.ConcurrencyConflict) ||
                attempt + 1 >= MaxReplaceAttempts)
            {
                return new SyncReport(
                    false,
                    true,
                    completedAt,
                    reports.ToImmutableArray(),
                    replaceResult.Errors.FirstOrDefault()?.Code,
                    "Unable to replace the full Host configuration.")
                {
                    FailureCode = SyncErrorCode.ReconcileFailed,
                    ConsumedSettingsVersion = consumedSettingsVersion
                };
            }
        }

        if (!replaceCompleted)
        {
            return new SyncReport(
                false,
                true,
                completedAt,
                reports.ToImmutableArray(),
                ConfigurationErrorCode.ConcurrencyConflict,
                "Unable to replace the full Host configuration.")
            {
                FailureCode = SyncErrorCode.ReconcileFailed,
                ConsumedSettingsVersion = consumedSettingsVersion
            };
        }

        var lockFailure = await PersistLockStateAsync(
                settings,
                desiredState.ConfigServiceNames,
                desiredState.ConfigYamls,
                desiredState.UpdatedLocks,
                reports,
                completedAt,
                consumedSettingsVersion,
                cancellationToken)
            .ConfigureAwait(false);
        if (lockFailure is not null)
        {
            return lockFailure;
        }

        var allServicesSucceeded = reports.All(report => report.Succeeded);
        return new SyncReport(
            allServicesSucceeded,
            true,
            DateTimeOffset.UtcNow,
            reports.ToImmutableArray(),
            null,
            null)
        {
            ConsumedSettingsVersion = consumedSettingsVersion
        };
    }

    private static bool HasError(IEnumerable<ConfigurationError> errors, ConfigurationErrorCode code) =>
        errors.Any(error => error.Code == code);

    private static SyncReport FailureReport(
        bool dataDirectoryAvailable,
        DateTimeOffset completedAt,
        ConfigurationErrorCode? code,
        string message,
        long? consumedSettingsVersion = null) =>
        new(
            false,
            dataDirectoryAvailable,
            completedAt,
            ImmutableArray<ServiceSyncReport>.Empty,
            code,
            message)
        {
            FailureCode = SyncErrorCode.ReconcileFailed,
            ConsumedSettingsVersion = consumedSettingsVersion
        };
}
