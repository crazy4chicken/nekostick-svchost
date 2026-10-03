using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;

namespace Nekostick.ServiceHost;

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

        SettingsStore? settingsStore;
        ApiKeyService? apiKeyService;
        IExtensionHostBridge13? bridge;
        ResetDriftForcedReconcileBackoff();
        lock (_lifecycleGate)
        {
            bridge = _bridge;
            settingsStore = _settingsStore;
            apiKeyService = _apiKeyService;
        }

        if (bridge is null || settingsStore is null)
        {
            return;
        }
        ObserveHostConfigurationVersion(bridge);
        try
        {
            var read = await settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (read.IsSuccess && read.Value is not null)
            {
                if (read.Value.Settings is not null)
                {
                    apiKeyService?.ReloadFromSettings(read.Value.Settings);
                }

                if (IsSelfSettledSettingsVersion(read.Value.Version))
                {
                    return;
                }
            }
            else if (await IsSelfSettledSettingsVersionAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        ScheduleDebouncedReconcile(cancellationToken, "settings-event");
    }

    private bool IsSelfSettledSettingsVersion(long version)
    {
        lock (_lifecycleGate)
        {
            return _bridge is null || _lastSelfSettledSettingsVersion == version;
        }
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

    private void ScheduleDebouncedReconcile(
        CancellationToken eventCancellationToken,
        string trigger,
        bool force = false,
        bool skipIfBusy = false)
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
            if (string.Equals(_debounceTrigger, "settings-event", StringComparison.Ordinal) &&
                trigger.StartsWith("drift-", StringComparison.Ordinal))
            {
                trigger = "settings-event";
            }

            _debounceTrigger = trigger;
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

        _ = DebounceAndReconcileAsync(current, force, skipIfBusy, trigger);
    }

    private async Task DebounceAndReconcileAsync(
        CancellationTokenSource debounceCancellation,
        bool force,
        bool skipIfBusy,
        string trigger)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), debounceCancellation.Token).ConfigureAwait(false);
            if (!force && await IsSelfSettledSettingsVersionAsync(debounceCancellation.Token).ConfigureAwait(false))
            {
                return;
            }

            if (skipIfBusy && IsReconcileInFlight())
            {
                return;
            }

            var report = await ReconcileTrackedAsync(
                    Array.Empty<Guid>(),
                    Array.Empty<Guid>(),
                    trigger,
                    debounceCancellation.Token)
                .ConfigureAwait(false);
            RecordSyncReport(report);
            if (force)
            {
                RecordDriftForcedReconcile(report);
            }
        }
        catch (OperationCanceledException) when (debounceCancellation.IsCancellationRequested)
        {
            // A newer event or extension stop superseded this debounce window.
        }
        catch (Exception exception)
        {
            IExtensionHostBridge13? bridge;
            lock (_lifecycleGate)
            {
                bridge = _bridge;
            }

            var report = new SyncReport(
                false,
                !string.IsNullOrWhiteSpace(bridge?.DataDirectory),
                DateTimeOffset.UtcNow,
                ImmutableArray<ServiceSyncReport>.Empty,
                null,
                exception.Message)
            {
                FailureCode = SyncErrorCode.ReconcileFailed,
                Trigger = trigger
            };
            RecordSyncReport(report);
            if (force)
            {
                RecordDriftForcedReconcile(report);
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
                    _debounceTrigger = null;
                }
            }

            debounceCancellation.Dispose();
        }
    }

    private void ObserveHostConfigurationVersion(IExtensionHostBridge13 bridge)
    {
        ExtensionHostInfoSnapshot hostInfo;
        try
        {
            hostInfo = bridge.HostInfo;
        }
        catch (Exception)
        {
            return;
        }

        RecordObservedHostConfigurationVersion(bridge, hostInfo.PublishedConfigurationVersion);
    }

    private void RecordObservedHostConfigurationVersion(
        IExtensionHostBridge13 bridge,
        long? publishedConfigurationVersion)
    {
        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_bridge, bridge))
            {
                _lastObservedPublishedConfigurationVersion = publishedConfigurationVersion;
            }
        }
    }

    private void StartDriftTimer()
    {
        lock (_lifecycleGate)
        {
            if (_bridge is null || _lifetimeCancellation is null || _driftTask is not null)
            {
                return;
            }

            var timer = new PeriodicTimer(DriftCheckInterval);
            var lifetimeToken = _lifetimeCancellation.Token;
            _driftTimer = timer;
            _driftTask = RunDriftTimerAsync(timer, lifetimeToken);
        }
    }

    private async Task RunDriftTimerAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await CheckConfigurationDriftAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    ReportDriftFailure();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Extension stop cancelled the periodic wait.
        }
        catch (Exception)
        {
            ReportDriftFailure();
        }
    }

    private async ValueTask CheckConfigurationDriftAsync(CancellationToken cancellationToken)
    {
        IExtensionHostBridge13? bridge;
        SettingsStore? settingsStore;
        long? lastPublishedVersion;
        long? lastConsumedSettingsVersion;
        bool reconcileUnsettled;
        bool driftForcedRetryDue;
        lock (_lifecycleGate)
        {
            bridge = _bridge;
            settingsStore = _settingsStore;
            lastPublishedVersion = _lastObservedPublishedConfigurationVersion;
            lastConsumedSettingsVersion = _lastReconcilerConsumedSettingsVersion;
            reconcileUnsettled = _reconcileUnsettled;
            if (_consecutiveDriftForcedReconcileCount == 0)
            {
                _driftForcedRetryTickCount = 0;
                driftForcedRetryDue = true;
            }
            else
            {
                _driftForcedRetryTickCount = Math.Min(
                    _driftForcedRetryTickCount + 1,
                    DriftForcedRetryMaximumIntervalTicks);
                driftForcedRetryDue = _driftForcedRetryTickCount >=
                    GetDriftForcedRetryIntervalTicks(_consecutiveDriftForcedReconcileCount);
            }
        }

        if (bridge is null || settingsStore is null)
        {
            return;
        }

        var hostInfo = bridge.HostInfo;
        if ((hostInfo.Readiness != ExtensionHostReadinessState.Ready &&
             hostInfo.Readiness != ExtensionHostReadinessState.Degraded) ||
            !hostInfo.SnapshotAvailable)
        {
            return;
        }

        var publishedVersion = hostInfo.PublishedConfigurationVersion;
        var hostVersionMismatch = publishedVersion.HasValue &&
            publishedVersion != lastPublishedVersion;
        var settingsRead = await settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!settingsRead.IsSuccess || settingsRead.Value is null)
        {
            bridge.Logger.Report(ExtensionLogLevel.Warning, "configuration-drift-settings-read-failed");
            if ((hostVersionMismatch || reconcileUnsettled) &&
                driftForcedRetryDue &&
                !IsReconcileInFlight())
            {
                ScheduleDebouncedReconcile(
                    cancellationToken,
                    trigger: reconcileUnsettled ? "drift-unsettled-retry" : "drift-host-version",
                    force: true,
                    skipIfBusy: true);
            }

            return;
        }

        var settingsVersionMismatch = !lastConsumedSettingsVersion.HasValue ||
            settingsRead.Value.Version != lastConsumedSettingsVersion.Value;
        var driftTrigger = reconcileUnsettled
            ? "drift-unsettled-retry"
            : hostVersionMismatch && !settingsVersionMismatch
                ? "drift-host-version"
                : "drift-settings-version";
        if ((!hostVersionMismatch && !settingsVersionMismatch && !reconcileUnsettled) ||
            !driftForcedRetryDue ||
            IsReconcileInFlight())
        {
            return;
        }

        ScheduleDebouncedReconcile(
            cancellationToken,
            trigger: driftTrigger,
            force: true,
            skipIfBusy: true);
    }

    private void ReportDriftFailure()
    {
        IExtensionHostBridge13? bridge;
        lock (_lifecycleGate)
        {
            bridge = _bridge;
        }

        bridge?.Logger.Report(ExtensionLogLevel.Warning, "configuration-drift-check-failed");
    }
}
