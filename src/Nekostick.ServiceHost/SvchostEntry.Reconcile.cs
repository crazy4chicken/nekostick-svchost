using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Api;
using Nekostick.ServiceHost.Logs;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;

namespace Nekostick.ServiceHost;

public sealed partial class SvchostEntry
{
    private const int MaximumReconcileLogLineLength = 512;
    private const int MaximumServiceDiffSummaryLength = 384;

    private async ValueTask<SyncReport> ReconcileTrackedAsync(
        IEnumerable<Guid> extraServiceIds,
        IEnumerable<Guid> extraRouteIds,
        string trigger,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        ReconcileStarted();
        SettingsStore? settingsStore = null;
        IExtensionHostBridge14? bridge = null;
        try
        {
            Reconciler? reconciler;
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
                    trigger,
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

            await WriteReconcileReportAsync(
                    bridge,
                    settingsStore,
                    report,
                    trigger,
                    Stopwatch.GetElapsedTime(startedAt))
                .ConfigureAwait(false);
            await ObserveReconciledServicesAsync(bridge, report, cancellationToken).ConfigureAwait(false);
            return report;
        }
        catch (Exception exception)
        {
            MarkReconcileUnsettled();
            WriteReconcileFailure(
                bridge,
                trigger,
                exception,
                Stopwatch.GetElapsedTime(startedAt));
            throw;
        }
        finally
        {
            ReconcileFinished();
        }
    }

    private async ValueTask ObserveReconciledServicesAsync(
        IExtensionHostBridge14? bridge,
        SyncReport report,
        CancellationToken cancellationToken)
    {
        if (bridge is null || report.Services.IsDefaultOrEmpty)
        {
            return;
        }

        try
        {
            SvchostApiHandler? apiHandler;
            lock (_lifecycleGate)
            {
                apiHandler = _apiHandler;
            }

            if (apiHandler is null)
            {
                return;
            }

            HashSet<Guid>? serviceIds = null;
            foreach (var service in report.Services)
            {
                if (service.ServiceId is not { } serviceId)
                {
                    continue;
                }

                serviceIds ??= new HashSet<Guid>();
                serviceIds.Add(serviceId);
            }

            if (serviceIds is null)
            {
                return;
            }

            var result = await bridge.Supervisor.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return;
            }

            var byId = result.Value.ToDictionary(snapshot => snapshot.ServiceId);
            foreach (var serviceId in serviceIds)
            {
                if (byId.TryGetValue(serviceId, out var runtime))
                {
                    apiHandler.RecordServiceObservation(serviceId, runtime.LifecycleState);
                }
                else
                {
                    apiHandler.ResetServiceObservation(serviceId);
                }
            }
        }
        catch (Exception)
        {
            // Status observations are best-effort and must not change the reconcile outcome.
        }
    }

    private static async ValueTask WriteReconcileReportAsync(
        IExtensionHostBridge14? bridge,
        SettingsStore? settingsStore,
        SyncReport report,
        string trigger,
        TimeSpan duration)
    {
        if (bridge is null)
        {
            return;
        }

        var logLevel = await ReadReconcileLogLevelAsync(settingsStore).ConfigureAwait(false);
        var updated = 0;
        var reused = 0;
        var preserved = 0;
        var failed = 0;
        var skipped = 0;
        if (!report.Services.IsDefault)
        {
            foreach (var service in report.Services)
            {
                switch (service.Decision)
                {
                    case ServiceDecision.Updated:
                        updated++;
                        break;
                    case ServiceDecision.Reused:
                        reused++;
                        break;
                    case ServiceDecision.Preserved:
                        preserved++;
                        break;
                    case ServiceDecision.Failed:
                        failed++;
                        break;
                    case ServiceDecision.Skipped:
                        skipped++;
                        break;
                }
            }
        }

        var hasFailures = report.HasFailures || failed > 0;
        var summary = CreateReconcileSummaryLine(
            trigger,
            report.Services.IsDefault ? 0 : report.Services.Length,
            updated,
            reused,
            preserved,
            failed,
            skipped,
            report.WrittenConfigurationVersion,
            duration,
            hasFailures ? GetReconcileFailureMessage(report) : null);
        if (hasFailures)
        {
            WriteReconcileLogLineBestEffort(bridge, ExtensionLogLevel.Warning, summary);
        }
        else if (logLevel == ExtensionLogLevel.Information)
        {
            WriteReconcileLogLineBestEffort(bridge, ExtensionLogLevel.Information, summary);
        }

        if (report.Services.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var service in report.Services)
        {
            if (logLevel == ExtensionLogLevel.Information && service.Decision != ServiceDecision.Reused)
            {
                WriteReconcileLogLineBestEffort(
                    bridge,
                    ExtensionLogLevel.Information,
                    CreateServiceDecisionLogLine(service));
            }

            if (IsExternalDriftWarning(trigger, service))
            {
                var driftLogLine = report.WrittenConfigurationVersion.HasValue
                    ? CreateExternalDriftLogLine(service)
                    : CreateUncommittedExternalDriftLogLine(service);
                WriteReconcileLogLineBestEffort(
                    bridge,
                    ExtensionLogLevel.Warning,
                    driftLogLine);
            }
        }
    }

    private static void WriteReconcileFailure(
        IExtensionHostBridge14? bridge,
        string trigger,
        Exception exception,
        TimeSpan duration)
    {
        if (bridge is null)
        {
            return;
        }

        var summary = CreateReconcileSummaryLine(trigger, 0, 0, 0, 0, 0, 0, null, duration, exception.Message);
        WriteReconcileLogLineBestEffort(bridge, ExtensionLogLevel.Warning, summary);
    }

    private static void WriteReconcileLogLineBestEffort(
        IExtensionHostBridge14 bridge,
        ExtensionLogLevel level,
        string message)
    {
        try
        {
            bridge.LogWriter.WriteText(level, BoundReconcileLogLine(message));
        }
        catch (Exception)
        {
            // Log failures must not change reconciliation outcomes.
        }
    }

    private static async ValueTask<ExtensionLogLevel> ReadReconcileLogLevelAsync(SettingsStore? settingsStore)
    {
        if (settingsStore is null)
        {
            return ExtensionLogLevel.Information;
        }

        try
        {
            var read = await settingsStore.ReadSettingsAsync().ConfigureAwait(false);
            return read.IsSuccess && string.Equals(
                    read.Value?.Settings?.Observability?.LogLevel,
                    "warning",
                    StringComparison.OrdinalIgnoreCase)
                ? ExtensionLogLevel.Warning
                : ExtensionLogLevel.Information;
        }
        catch (Exception)
        {
            return ExtensionLogLevel.Information;
        }
    }

    private static string CreateReconcileSummaryLine(
        string trigger,
        int evaluated,
        int updated,
        int reused,
        int preserved,
        int failed,
        int skipped,
        long? writtenConfigurationVersion,
        TimeSpan duration,
        string? error)
    {
        var version = writtenConfigurationVersion?.ToString(CultureInfo.InvariantCulture) ?? "-";
        var durationMilliseconds = duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
        var summary = FormattableString.Invariant(
            $"reconcile trigger={trigger} evaluated={evaluated} updated={updated} reused={reused} preserved={preserved} failed={failed} skipped={skipped} written={(writtenConfigurationVersion.HasValue ? "true" : "false")} version={version} duration={durationMilliseconds}");
        return string.IsNullOrWhiteSpace(error) ? summary : $"{summary} error={LimitLogText(error, 256)}";
    }

    private static string GetReconcileFailureMessage(SyncReport report)
    {
        if (!string.IsNullOrWhiteSpace(report.Error))
        {
            return report.Error;
        }

        if (!report.Services.IsDefault)
        {
            foreach (var service in report.Services)
            {
                if ((service.Decision == ServiceDecision.Failed || !service.Succeeded) &&
                    !string.IsNullOrWhiteSpace(service.Error))
                {
                    return service.Error;
                }
            }
        }

        return "one or more services failed";
    }

    private static string CreateServiceDecisionLogLine(ServiceSyncReport service)
    {
        var detail = service.Diffs.IsDefaultOrEmpty
            ? string.IsNullOrWhiteSpace(service.Error) ? "no field changes" : LimitLogText(service.Error, 256)
            : FormatServiceDiffSummary(service);
        var decision = service.Decision.ToString().ToLowerInvariant();
        return $"service {LimitLogText(service.ConfigName, 64)}/{LimitLogText(service.ServiceName, 96)}: {decision} {detail}";
    }

    private static string CreateExternalDriftLogLine(ServiceSyncReport service) =>
        $"external drift corrected on {LimitLogText(service.ConfigName, 64)}/{LimitLogText(service.ServiceName, 96)}: {FormatServiceDiffSummary(service)}";
    private static string CreateUncommittedExternalDriftLogLine(ServiceSyncReport service) =>
        $"external drift detected, correction not committed for {LimitLogText(service.ConfigName, 64)}/{LimitLogText(service.ServiceName, 96)}: {FormatServiceDiffSummary(service)}";

    private static string FormatServiceDiffSummary(ServiceSyncReport service)
    {
        var builder = new StringBuilder(MaximumServiceDiffSummaryLength);
        foreach (var diff in service.Diffs)
        {
            var fieldDiff = $"{LimitLogText(diff.Field, 64)} {LimitLogText(diff.OldValue, 96)} -> {LimitLogText(diff.NewValue, 96)}";
            if (builder.Length > 0)
            {
                fieldDiff = $"; {fieldDiff}";
            }

            var remaining = MaximumServiceDiffSummaryLength - builder.Length;
            if (fieldDiff.Length > remaining)
            {
                var maximumContentLength = MaximumServiceDiffSummaryLength - 3;
                if (builder.Length > maximumContentLength)
                {
                    builder.Length = maximumContentLength;
                }
                else if (fieldDiff.Length > maximumContentLength - builder.Length)
                {
                    builder.Append(fieldDiff, 0, maximumContentLength - builder.Length);
                }

                builder.Append("...");
                break;
            }

            builder.Append(fieldDiff);
        }

        return builder.ToString();
    }

    private static bool IsExternalDriftWarning(string trigger, ServiceSyncReport service) =>
        ExternalDriftWarning.IsDriftCorrection(trigger, service);

    private static string LimitLogText(string? text, int maximumLength)
    {
        if (text is null || text.Length <= maximumLength)
        {
            return text ?? "-";
        }

        return $"{text[..(maximumLength - 3)]}...";
    }

    private static string BoundReconcileLogLine(string message)
    {
        var truncated = message.Length > MaximumReconcileLogLineLength;
        var contentLength = truncated ? MaximumReconcileLogLineLength - 3 : message.Length;
        var hasLineBreak = false;
        for (var index = 0; index < contentLength; index++)
        {
            if (message[index] is '\r' or '\n')
            {
                hasLineBreak = true;
                break;
            }
        }

        if (!truncated && !hasLineBreak)
        {
            return message;
        }

        var builder = new StringBuilder(Math.Min(message.Length, MaximumReconcileLogLineLength));
        for (var index = 0; index < contentLength; index++)
        {
            builder.Append(message[index] is '\r' or '\n' ? ' ' : message[index]);
        }

        if (truncated)
        {
            builder.Append("...");
        }

        return builder.ToString();
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
        IExtensionHostBridge14? bridge;
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
