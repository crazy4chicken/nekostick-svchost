using Nekolla.Nekostick.Contracts;
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
                ObserveHostConfigurationVersion(bridge);
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
                    _unsettledDriftTickCount = 0;
                }
                else if (consumedSettingsVersion.HasValue)
                {
                    _reconcileUnsettled = true;
                    _unsettledDriftTickCount = 0;
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
                _unsettledDriftTickCount = 0;
            }
        }
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
        lock (_lifecycleGate)
        {
            bridge = _bridge;
            startupDegraded = _startupDegraded;
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
    }
}
