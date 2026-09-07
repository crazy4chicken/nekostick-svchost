using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.ServiceHost.Settings;
using Nekolla.Nekostick.ServiceHost.Sync;

namespace Nekolla.Nekostick.ServiceHost;

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
            lock (_lifecycleGate)
            {
                reconciler = _reconciler;
                settingsStore = _settingsStore;
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
            if (report.Succeeded && settingsStore is not null)
            {
                await StashSettledSettingsVersionAsync(
                        settingsStore,
                        report.ConsumedSettingsVersion)
                    .ConfigureAwait(false);
            }

            return report;
        }
        finally
        {
            ReconcileFinished();
        }
    }

    private ValueTask StashSettledSettingsVersionAsync(
        SettingsStore settingsStore,
        long? consumedSettingsVersion)
    {
        if (!consumedSettingsVersion.HasValue)
        {
            return ValueTask.CompletedTask;
        }

        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_settingsStore, settingsStore))
            {
                _lastSelfSettledSettingsVersion = consumedSettingsVersion.Value;
            }
        }

        return ValueTask.CompletedTask;
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
