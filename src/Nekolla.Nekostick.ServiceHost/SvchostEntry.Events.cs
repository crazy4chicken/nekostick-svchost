using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.ServiceHost.Settings;

namespace Nekolla.Nekostick.ServiceHost;

public sealed partial class SvchostEntry
{
    private async ValueTask OnHostEventAsync(ExtensionEvent @event, CancellationToken cancellationToken)
    {
        if (!string.Equals(
                @event.Type,
                nameof(ExtensionCoreEventKind.ExtensionSettingsChanged),
                StringComparison.Ordinal))
        {
            return;
        }

        if (await IsSelfSettledSettingsVersionAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        ScheduleDebouncedReconcile(cancellationToken);
    }

    private async ValueTask<bool> IsSelfSettledSettingsVersionAsync(CancellationToken cancellationToken)
    {
        SettingsStore? settingsStore;
        long? lastSettledVersion;
        lock (_lifecycleGate)
        {
            if (_bridge is null)
            {
                return true;
            }

            settingsStore = _settingsStore;
            lastSettledVersion = _lastSelfSettledSettingsVersion;
        }

        if (settingsStore is null || !lastSettledVersion.HasValue)
        {
            return false;
        }

        try
        {
            var read = await settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            return read.IsSuccess && read.Value?.Version == lastSettledVersion.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void ScheduleDebouncedReconcile(CancellationToken eventCancellationToken)
    {
        CancellationToken lifetimeToken;
        lock (_lifecycleGate)
        {
            if (_bridge is null)
            {
                return;
            }

            lifetimeToken = _lifetimeCancellation?.Token ?? CancellationToken.None;
        }

        CancellationTokenSource? previous;
        CancellationTokenSource current;
        lock (_debounceGate)
        {
            previous = _debounceCancellation;
            current = CancellationTokenSource.CreateLinkedTokenSource(
                eventCancellationToken,
                lifetimeToken);
            _debounceCancellation = current;
        }

        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The superseded debounce may have completed between the two locks.
        }

        _ = DebounceAndReconcileAsync(current);
    }
    private async Task DebounceAndReconcileAsync(CancellationTokenSource debounceCancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), debounceCancellation.Token).ConfigureAwait(false);
            if (await IsSelfSettledSettingsVersionAsync(debounceCancellation.Token).ConfigureAwait(false))
            {
                return;
            }

            var report = await ReconcileTrackedAsync(
                    Array.Empty<Guid>(),
                    Array.Empty<Guid>(),
                    debounceCancellation.Token)
                .ConfigureAwait(false);
            ObserveSync(report);
        }
        catch (OperationCanceledException) when (debounceCancellation.IsCancellationRequested)
        {
            // A newer event or extension stop superseded this debounce window.
        }
        catch (Exception)
        {
            IExtensionHostBridge13? bridge;
            lock (_lifecycleGate)
            {
                bridge = _bridge;
            }

            bridge?.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "settings-sync-failed"));
        }
        finally
        {
            lock (_debounceGate)
            {
                if (ReferenceEquals(_debounceCancellation, debounceCancellation))
                {
                    _debounceCancellation = null;
                }
            }

            debounceCancellation.Dispose();
        }
    }
}
