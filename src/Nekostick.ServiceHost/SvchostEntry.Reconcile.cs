using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Logs;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;

namespace Nekostick.ServiceHost;

public sealed partial class SvchostEntry
{
    private async ValueTask<SyncReport> ReconcileTrackedAsync(
        IEnumerable<Guid> extraServiceIds,
        IEnumerable<Guid> extraRouteIds,
        CancellationToken cancellationToken)
    {
        ReconcileStarted();
        try
        {
            Reconciler? reconciler;
            SettingsStore? settingsStore;
            IExtensionHostBridge13? bridge;
            lock (_lifecycleGate)
            {
                reconciler = _reconciler;
                settingsStore = _settingsStore;
                bridge = _bridge;
            }

            if (reconciler is null)
            {
                throw new InvalidOperationException("The extension reconciler is unavailable.");
            }

            var report = await reconciler.ReconcileAsync(
                    extraServiceIds,
                    extraRouteIds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (settingsStore is not null)
            {
                await StashSettledSettingsVersionAsync(
                        settingsStore,
                        report.ConsumedSettingsVersion,
                        report.Succeeded)
                    .ConfigureAwait(false);
            }

            if (bridge is not null)
            {
                if (report.WrittenConfigurationVersion is { } writtenVersion)
                {
                    RecordObservedHostConfigurationVersion(bridge, writtenVersion);
                }
                else
                {
                    ObserveHostConfigurationVersion(bridge);
                }
            }

            if (!report.HasFailures && !report.WrittenConfigurationVersion.HasValue)
            {
                ResetDriftForcedReconcileBackoff();
            }

            return report;
        }
        catch
        {
            MarkReconcileUnsettled();
            throw;
        }
        finally
        {
            ReconcileFinished();
        }
    }

    private ValueTask StashSettledSettingsVersionAsync(
        SettingsStore settingsStore,
        long? consumedSettingsVersion,
        bool settled)
    {
        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_settingsStore, settingsStore))
            {
                if (consumedSettingsVersion.HasValue)
                {
                    _lastReconcilerConsumedSettingsVersion = consumedSettingsVersion.Value;
                    if (settled)
                    {
                        _lastSelfSettledSettingsVersion = consumedSettingsVersion.Value;
                    }
                }

                if (settled)
                {
                    _reconcileUnsettled = false;
                    _driftForcedRetryTickCount = 0;
                }
                else if (consumedSettingsVersion.HasValue)
                {
                    _reconcileUnsettled = true;
                    _driftForcedRetryTickCount = 0;
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private void MarkReconcileUnsettled()
    {
        lock (_lifecycleGate)
        {
            if (_bridge is not null)
            {
                _reconcileUnsettled = true;
                _driftForcedRetryTickCount = 0;
            }
        }
    }

    private void ResetDriftForcedReconcileBackoff()
    {
        lock (_lifecycleGate)
        {
            _consecutiveDriftForcedReconcileCount = 0;
            _driftForcedRetryTickCount = 0;
        }
    }

    private void RecordDriftForcedReconcile(SyncReport report)
    {
        lock (_lifecycleGate)
        {
            if (!report.HasFailures && !report.WrittenConfigurationVersion.HasValue)
            {
                _consecutiveDriftForcedReconcileCount = 0;
                _driftForcedRetryTickCount = 0;
                return;
            }

            _consecutiveDriftForcedReconcileCount = Math.Min(
                _consecutiveDriftForcedReconcileCount + 1,
                DriftForcedRetryMaximumIntervalTicks);
            _driftForcedRetryTickCount = 0;
        }
    }

    private static int GetDriftForcedRetryIntervalTicks(int consecutiveReconcileCount)
    {
        var retryIntervalTicks = DriftForcedRetryBaseIntervalTicks;
        for (var retry = 1;
             retry < consecutiveReconcileCount && retryIntervalTicks < DriftForcedRetryMaximumIntervalTicks;
             retry++)
        {
            retryIntervalTicks = Math.Min(
                retryIntervalTicks * 2,
                DriftForcedRetryMaximumIntervalTicks);
        }

        return retryIntervalTicks;
    }

    private void ReconcileStarted()
    {
        lock (_reconcileGate)
        {
            if (_activeReconciles == 0)
            {
                _reconcileIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _activeReconciles++;
        }
    }

    private void ReconcileFinished()
    {
        lock (_reconcileGate)
        {
            _activeReconciles--;
            if (_activeReconciles == 0)
            {
                _reconcileIdle.TrySetResult(true);
            }
        }
    }

    private bool IsReconcileInFlight()
    {
        lock (_reconcileGate)
        {
            return _activeReconciles > 0;
        }
    }

    private Task GetReconcileIdleTask()
    {
        lock (_reconcileGate)
        {
            return _activeReconciles == 0 ? CompletedTask : _reconcileIdle.Task;
        }
    }

    private void MarkStartupDegraded()
    {
        lock (_lifecycleGate)
        {
            _startupDegraded = true;
        }
    }

    private void ObserveSync(SyncReport report)
    {
        IExtensionHostBridge13? bridge;
        bool startupDegraded;
        ServiceLogRecorder? logRecorder;
        lock (_lifecycleGate)
        {
            bridge = _bridge;
            startupDegraded = _startupDegraded;
            logRecorder = _logRecorder;
        }

        if (bridge is null)
        {
            return;
        }

        var healthy = report.Succeeded && !startupDegraded;
        bridge.Status.Report(
            new ExtensionStatus(
                healthy ? ExtensionStatusKind.Healthy : ExtensionStatusKind.Degraded,
                healthy ? "sync-healthy" : "sync-failed"));
        if (!report.Succeeded && report.Services.IsEmpty)
        {
            return;
        }

        var targets = new List<ServiceLogTarget>();
        foreach (var service in report.Services)
        {
            if (service.ServiceId is { } id && service.LogDirectory is { } dir)
            {
                targets.Add(new ServiceLogTarget(id, service.ServiceName, dir));
            }
        }

        logRecorder?.SyncTargets(targets);
    }
}
