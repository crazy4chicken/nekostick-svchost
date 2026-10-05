using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Api;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Logs;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Nekostick.ServiceHost.Sync.Releases;
using Nekostick.ServiceHost.Webui;

namespace Nekostick.ServiceHost;

/// <summary>Entrypoint for the svchost management API and WebUI extension.</summary>
public sealed partial class SvchostEntry : IExtensionEntry
{
    private static readonly Task CompletedTask = Task.CompletedTask;
    private static readonly HostApiVersion MinimumHostApiVersion = new(1, 4, 0);
    private const int DriftForcedRetryBaseIntervalTicks = 1;
    private const int DriftForcedRetryMaximumIntervalTicks = 16;
    private static readonly TimeSpan DriftCheckInterval = TimeSpan.FromSeconds(60);
    private readonly object _lifecycleGate = new();
    private readonly object _debounceGate = new();
    private readonly object _reconcileGate = new();

    private IExtensionHostBridge14? _bridge;
    private IExtensionRegistration? _registration;
    private SettingsStore? _settingsStore;
    private ApiKeyService? _apiKeyService;
    private Reconciler? _reconciler;
    private SvchostApiHandler? _apiHandler;
    private ServiceLogRecorder? _logRecorder;
    private CancellationTokenSource? _lifetimeCancellation;
    private CancellationTokenSource? _debounceCancellation;
    private string? _debounceTrigger;
    private PeriodicTimer? _driftTimer;
    private Task? _driftTask;
    private long? _lastSelfSettledSettingsVersion;
    private long? _lastReconcilerConsumedSettingsVersion;
    private long? _lastObservedPublishedConfigurationVersion;
    private bool _reconcileUnsettled;
    private int _driftForcedRetryTickCount;
    private int _consecutiveDriftForcedReconcileCount;
    private bool _startupDegraded;
    private TaskCompletionSource<bool> _reconcileIdle = CreateCompletedSource();
    private int _activeReconciles;

    /// <inheritdoc />
    public async ValueTask StartAsync(
        IExtensionStartContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_lifecycleGate)
        {
            if (_bridge is not null)
            {
                return;
            }
        }

        var host = context.Host;
        if (!ExtensionAbi.IsCompatible(MinimumHostApiVersion, host.ApiVersion))
        {
            host.Logger.Report(ExtensionLogLevel.Warning, "api-14-unsupported");
            return;
        }

        if (host is not IExtensionHostBridge14 bridge)
        {
            host.Logger.Report(ExtensionLogLevel.Warning, "api-14-bridge-unavailable");
            return;
        }

        var outputApi = bridge.ServiceOutput;
        if (!bridge.HostInfo.ReadOnly)
        {
            LegacySettingsMigrationResult migrationResult;
            try
            {
                migrationResult = await new LegacySettingsMigration(bridge.FullConfiguration)
                    .MigrateAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                migrationResult = LegacySettingsMigrationResult.Failed;
            }

            var migrationLogLevel = migrationResult == LegacySettingsMigrationResult.Failed
                ? ExtensionLogLevel.Warning
                : ExtensionLogLevel.Information;
            var migrationLogCode = migrationResult switch
            {
                LegacySettingsMigrationResult.Migrated => "legacy-settings-migrated",
                LegacySettingsMigrationResult.SkippedExisting => "legacy-settings-migration-skipped-existing",
                LegacySettingsMigrationResult.None => "legacy-settings-not-found",
                _ => "legacy-settings-migration-failed"
            };
            bridge.Logger.Report(migrationLogLevel, migrationLogCode);
        }

        var settingsStore = new SettingsStore(host.ConfigurationApi);
        var apiKeyService = new ApiKeyService(settingsStore, bridge);
        ApiKeyInitializationResult initialization;
        try
        {
            initialization = await apiKeyService.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            bridge.Logger.Report(ExtensionLogLevel.Warning, "settings-initialization-failed");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "settings-initialization-failed"));
            return;
        }
        var logRecorder = new ServiceLogRecorder(outputApi, bridge.Logger, bridge.LogWriter);
        lock (_lifecycleGate)
        {
            _bridge = bridge;
            _logRecorder = logRecorder;
            _registration = context.Registration;
            _settingsStore = settingsStore;
            _apiKeyService = apiKeyService;
            _lifetimeCancellation = new CancellationTokenSource();
            _debounceCancellation = null;
            _debounceTrigger = null;
            _driftTimer = null;
            _driftTask = null;
            _lastSelfSettledSettingsVersion = null;
            _lastReconcilerConsumedSettingsVersion = null;
            _lastObservedPublishedConfigurationVersion = null;
            _reconcileUnsettled = false;
            _driftForcedRetryTickCount = 0;
            _consecutiveDriftForcedReconcileCount = 0;
            _startupDegraded = false;
            _reconcileIdle = CreateCompletedSource();
            _activeReconciles = 0;
        }

        if (!initialization.Succeeded)
        {
            MarkStartupDegraded();
            bridge.Logger.Report(ExtensionLogLevel.Warning, "settings-unavailable");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "settings-unavailable"));
            return;
        }
        
        if (initialization.Readonly)
        {
            MarkStartupDegraded();
            bridge.Logger.Report(ExtensionLogLevel.Warning, "settings-readonly-routes-skipped");
            bridge.LogWriter.WriteText(
                ExtensionLogLevel.Warning,
                "nekostick.svchost settings are read-only; API and WebUI handlers/routes were not registered.");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "settings-readonly"));
            return;
        }
        
        if (initialization.Settings is null)
        {
            MarkStartupDegraded();
            bridge.Logger.Report(ExtensionLogLevel.Warning, "settings-missing");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "settings-missing"));
            return;
        }
        var staleRoutesRemoved = await RemoveStaleHandlerRoutesAsync(
                bridge,
                initialization.Settings.Routes,
                cancellationToken)
            .ConfigureAwait(false);
        if (!staleRoutesRemoved)
        {
            MarkStartupDegraded();
            bridge.Logger.Report(ExtensionLogLevel.Warning, "handler-route-cleanup-failed");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "handler-route-cleanup-failed"));
            return;
        }

        var composeParser = new ComposeFileParser();
        var releaseProviders = new ReleaseProviderRegistry([new GitHubReleaseProvider()]);
        var reconciler = new Reconciler(
            settingsStore,
            composeParser,
            new SourceResolver(releaseProviders: releaseProviders),
            bridge.FullConfiguration,
            bridge.DataDirectory,
            bridge.Supervisor);
        var apiHandler = new SvchostApiHandler(
            apiKeyService,
            settingsStore,
            composeParser,
            reconciler,
            bridge,
            ReconcileTrackedAsync,
            ObserveSync,
            logRecorder: logRecorder);
        var webuiHandler = new WebuiHandler();

        lock (_lifecycleGate)
        {
            _reconciler = reconciler;
            _apiHandler = apiHandler;
        }

        var apiRegistration = context.Registration.TryRegisterStreamingHandler(apiHandler);
        var webuiRegistration = context.Registration.TryRegisterStreamingHandler(webuiHandler);
        ReportRegistrationFailure(bridge, apiRegistration, "register", SvchostApiHandler.StableHandlerId);
        ReportRegistrationFailure(bridge, webuiRegistration, "register", WebuiHandler.StableHandlerId);
        if (!apiRegistration.Succeeded || !webuiRegistration.Succeeded)
        {
            if (apiRegistration.Succeeded)
            {
                UnregisterHandler(bridge, context.Registration, SvchostApiHandler.StableHandlerId);
            }

            if (webuiRegistration.Succeeded)
            {
                UnregisterHandler(bridge, context.Registration, WebuiHandler.StableHandlerId);
            }

            MarkStartupDegraded();
            bridge.Logger.Report(ExtensionLogLevel.Warning, "handler-registration-failed");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "handler-registration-failed"));
            return;
        }

        var routeResult = await UpsertHandlerRoutesAsync(
                bridge,
                initialization.Settings.Routes,
                cancellationToken)
            .ConfigureAwait(false);
        if (!routeResult)
        {
            UnregisterHandler(bridge, context.Registration, SvchostApiHandler.StableHandlerId);
            UnregisterHandler(bridge, context.Registration, WebuiHandler.StableHandlerId);
            MarkStartupDegraded();
            bridge.Logger.Report(ExtensionLogLevel.Warning, "handler-route-registration-failed");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "handler-route-registration-failed"));
            return;
        }

        var startupDegraded = false;
        var eventSubscription = host.Events.TrySubscribe(OnHostEventAsync);
        if (eventSubscription is ExtensionEventSubscribeFailureResult subscriptionFailure)
        {
            startupDegraded = true;
            MarkStartupDegraded();
            bridge.Logger.Report(ExtensionLogLevel.Warning, "settings-event-subscription-failed");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "settings-event-subscription-failed"));
            WriteContractFailureBestEffort(
                bridge,
                $"Settings event subscription failed with {subscriptionFailure.Code}: {subscriptionFailure.Detail.Message}");
        }

        bool taskAccepted;
        try
        {
            var taskResult = await bridge.Tasks.StartAsync("sync", InitialReconcileAsync).ConfigureAwait(false);
            taskAccepted = taskResult.Succeeded;
            if (taskResult is ExtensionTaskStartFailureResult taskFailure)
            {
                WriteContractFailureBestEffort(
                    bridge,
                    $"Initial sync scheduling failed with {taskFailure.Code}: {taskFailure.Detail.Message}");
            }
        }
        catch (Exception exception)
        {
            taskAccepted = false;
            WriteContractFailureBestEffort(
                bridge,
                $"Initial sync scheduling threw: {ExtensionErrorDetail.FromException(exception).Message}");
        }

        if (!taskAccepted)
        {
            MarkStartupDegraded();
            bridge.Logger.Report(ExtensionLogLevel.Warning, "initial-sync-not-scheduled");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "initial-sync-not-scheduled"));
        }
        else if (!startupDegraded)
        {
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Healthy, "ready"));
        }
        ObserveHostConfigurationVersion(bridge);
        StartDriftTimer();
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        IExtensionHostBridge14? bridge;
        IExtensionRegistration? registration;
        SvchostApiHandler? apiHandler;
        ServiceLogRecorder? logRecorder;
        CancellationTokenSource? lifetimeCancellation;
        CancellationTokenSource? debounceCancellation;
        PeriodicTimer? driftTimer;
        Task driftTask;
        Task idleTask;

        lock (_lifecycleGate)
        {
            bridge = _bridge;
            registration = _registration;
            apiHandler = _apiHandler;
            logRecorder = _logRecorder;
            lifetimeCancellation = _lifetimeCancellation;
            idleTask = GetReconcileIdleTask();
            debounceCancellation = _debounceCancellation;
            driftTimer = _driftTimer;
            driftTask = _driftTask ?? CompletedTask;
            _bridge = null;
            _registration = null;
            _settingsStore = null;
            _apiKeyService = null;
            _reconciler = null;
            _apiHandler = null;
            _logRecorder = null;
            _lifetimeCancellation = null;
            _debounceCancellation = null;
            _debounceTrigger = null;
            _driftTimer = null;
            _driftTask = null;
            _lastSelfSettledSettingsVersion = null;
            _lastReconcilerConsumedSettingsVersion = null;
            _lastObservedPublishedConfigurationVersion = null;
            _reconcileUnsettled = false;
            _driftForcedRetryTickCount = 0;
            _consecutiveDriftForcedReconcileCount = 0;
        }

        if (bridge is null)
        {
            return;
        }

        try
        {
            lifetimeCancellation?.Cancel();
            debounceCancellation?.Cancel();
            driftTimer?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // A concurrently completing debounce or timer may already be disposed.
        }

        apiHandler?.Cancel();

        if (registration is not null)
        {
            UnregisterHandler(bridge, registration, SvchostApiHandler.StableHandlerId);
            UnregisterHandler(bridge, registration, WebuiHandler.StableHandlerId);
        }

        try
        {
            var stopTask = Task.WhenAll(idleTask, driftTask);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            await Task.WhenAny(stopTask, timeoutTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop remains best effort after the caller's cancellation request.
        }
        finally
        {
            try
            {
                logRecorder?.Dispose();
            }
            catch (Exception)
            {
                // Recorder disposal is best effort during shutdown.
            }

            apiHandler?.Cancel();
            apiHandler?.Dispose();
            debounceCancellation?.Dispose();
            lifetimeCancellation?.Dispose();
        }
    }

    private static void UnregisterHandler(
        IExtensionHostBridge14 bridge,
        IExtensionRegistration registration,
        string handlerId) =>
        ReportRegistrationFailure(bridge, registration.TryUnregisterHandler(handlerId), "unregister", handlerId);

    private static void ReportRegistrationFailure(
        IExtensionHostBridge14 bridge,
        ExtensionRegistrationResult result,
        string operation,
        string handlerId)
    {
        if (result is ExtensionRegistrationFailureResult failure)
        {
            WriteContractFailureBestEffort(
                bridge,
                $"Handler '{handlerId}' {operation} failed with {failure.Code}: {failure.Detail.Message}");
        }
    }

    private static void WriteContractFailureBestEffort(IExtensionHostBridge14 bridge, string message)
    {
        try
        {
            bridge.LogWriter.WriteText(ExtensionLogLevel.Warning, message);
        }
        catch (Exception)
        {
            // Failure diagnostics must not change startup or cleanup outcomes.
        }
    }

    private async ValueTask InitialReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await ReconcileTrackedAsync(
                    Array.Empty<Guid>(),
                    Array.Empty<Guid>(),
                    "startup",
                    cancellationToken)
                .ConfigureAwait(false);
            RecordSyncReport(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The Host task scheduler cancels this callback during extension stop.
        }
        catch (Exception exception)
        {
            IExtensionHostBridge14? bridge;
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
                Trigger = "startup"
            };
            RecordSyncReport(report);
            bridge?.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "initial-sync-failed"));
        }
    }

    private void RecordSyncReport(SyncReport report)
    {
        SvchostApiHandler? apiHandler;
        lock (_lifecycleGate)
        {
            apiHandler = _apiHandler;
        }

        if (apiHandler is null)
        {
            ObserveSync(report);
            return;
        }

        apiHandler.RecordReport(report);
    }

    private static TaskCompletionSource<bool> CreateCompletedSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult(true);
        return source;
    }
}
