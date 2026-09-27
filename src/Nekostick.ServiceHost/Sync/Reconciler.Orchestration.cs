using System.Collections.Immutable;
using System.Diagnostics;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.Sync;

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
        var desiredServiceIds = desiredServices.Select(service => service.Id).ToHashSet();
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
        var replaceSucceeded = false;
        var orphanNotes = ImmutableArray<string>.Empty;
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
                    ConsumedSettingsVersion = consumedSettingsVersion,
                    Notes = orphanNotes
                };
            }

            var snapshot = snapshotResult.Value;
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
            foreach (var index in Enumerable.Range(0, reports.Count).ToArray())
            {
                var report = reports[index];
                if (report.FailureCode == SyncErrorCode.SourceFailed && report.ServiceId is { } serviceId)
                {
                    reports[index] = report with
                    {
                        NodeLocal = preservedSourceServiceIds.Contains(serviceId)
                    };
                }
            }

            var orphanSweep = FindOrphanSweep(
                snapshot,
                desiredState.ConfiguredLockServiceIds,
                desiredServiceIds);
            orphanNotes = orphanSweep.Notes;
            var changes = BuildChangeSet(
                snapshot,
                desiredState.ManagedServiceIds,
                desiredState.ManagedRouteIds,
                desiredServices.Select(service => PreserveServiceVersion(
                    service,
                    snapshot,
                    refreshedServiceIds.Contains(service.Id))),
                desiredRoutes.Select(route => PreserveRouteVersion(route, snapshot)),
                preservedSourceServiceIds,
                preservedSourceRouteIds,
                desiredState.ConfiguredLockServiceIds);
            if (!sourceChanged &&
                SemanticallyEqualIgnoringVersion(snapshot.Services, changes.Services) &&
                SemanticallyEqualIgnoringVersion(snapshot.Routes, changes.Routes))
            {
                replaceCompleted = true;
                break;
            }

            // The desired state was built from the settings version consumed at the
            // start of this reconcile. Abort before every publish if settings changed.
            var latestSettingsResult = await _settingsStore.ReadSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!latestSettingsResult.IsSuccess || latestSettingsResult.Value is null)
            {
                return new SyncReport(
                    false,
                    true,
                    completedAt,
                    reports.ToImmutableArray(),
                    latestSettingsResult.Errors.FirstOrDefault()?.Code,
                    "Unable to re-read extension settings before replacing Host configuration.")
                {
                    FailureCode = SyncErrorCode.ReconcileFailed,
                    ConsumedSettingsVersion = consumedSettingsVersion,
                    Notes = orphanNotes
                };
            }

            if (latestSettingsResult.Value.Version != consumedSettingsVersion)
            {
                return new SyncReport(
                    false,
                    true,
                    completedAt,
                    reports.ToImmutableArray(),
                    ConfigurationErrorCode.ConcurrencyConflict,
                    "Extension settings changed during reconciliation; retry synchronization.")
                {
                    FailureCode = SyncErrorCode.ReconcileFailed,
                    ConsumedSettingsVersion = consumedSettingsVersion,
                    Notes = orphanNotes
                };
            }

            var replaceResult = await _fullConfiguration.ReplaceAsync(
                    snapshot.Version,
                    changes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replaceResult.IsSuccess)
            {
                replaceCompleted = true;
                replaceSucceeded = true;
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
                    ConsumedSettingsVersion = consumedSettingsVersion,
                    Notes = orphanNotes
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
                ConsumedSettingsVersion = consumedSettingsVersion,
                Notes = orphanNotes
            };
        }

        if (replaceSucceeded)
        {
            await ResumeWaitingServicesBestEffortAsync(
                    desiredState.ConfiguredLockServiceIds,
                    desiredState.UpdatedLocks.Values,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var lockFailure = await PersistLockStateAsync(
                settings,
                desiredState.ConfigServiceNames,
                desiredState.ConfigYamls,
                desiredState.UpdatedLocks,
                reports,
                completedAt,
                consumedSettingsVersion,
                orphanNotes,
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
            ConsumedSettingsVersion = consumedSettingsVersion,
            Notes = orphanNotes
        };
    }

    private async ValueTask ResumeWaitingServicesBestEffortAsync(
        IEnumerable<Guid> configuredLockServiceIds,
        IEnumerable<LockServiceEntry> updatedLocks,
        CancellationToken cancellationToken)
    {
        if (_supervisor is null)
        {
            return;
        }

        var managedServiceIds = configuredLockServiceIds.ToHashSet();
        foreach (var lockEntry in updatedLocks)
        {
            if (lockEntry is not null && IsUuidV7(lockEntry.ServiceId))
            {
                managedServiceIds.Add(lockEntry.ServiceId);
            }
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
                             managedServiceIds.Contains(snapshot.ServiceId) &&
                             snapshot.LifecycleState == ExtensionServiceLifecycleState.Waiting)
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
