using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Api;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Nekostick.ServiceHost.Sync.Releases;
using Nekostick.ServiceHost.Webui;

namespace Nekostick.ServiceHost;

/// <summary>Entrypoint for the svchost management API and WebUI extension.</summary>
public sealed partial class SvchostEntry : IExtensionEntry
{
    private static readonly Task CompletedTask = Task.CompletedTask;
    private static readonly HostApiVersion MinimumHostApiVersion = new(1, 3, 3);
    private const int UnsettledDriftRetryIntervalTicks = 4;
    private static readonly TimeSpan DriftCheckInterval = TimeSpan.FromSeconds(60);
    private readonly object _lifecycleGate = new();
    private readonly object _debounceGate = new();
    private readonly object _reconcileGate = new();

    private IExtensionHostBridge13? _bridge;
    private IExtensionRegistration? _registration;
    private SettingsStore? _settingsStore;
    private ApiKeyService? _apiKeyService;
    private Reconciler? _reconciler;
    private SvchostApiHandler? _apiHandler;
    private CancellationTokenSource? _lifetimeCancellation;
    private CancellationTokenSource? _debounceCancellation;
    private PeriodicTimer? _driftTimer;
    private Task? _driftTask;
    private long? _lastSelfSettledSettingsVersion;
    private long? _lastReconcilerConsumedSettingsVersion;
    private long? _lastObservedPublishedConfigurationVersion;
    private bool _reconcileUnsettled;
    private int _unsettledDriftTickCount;
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
            host.Logger.Report(ExtensionLogLevel.Warning, "api-13-unsupported");
            return;
        }

        if (host is not IExtensionHostBridge13 bridge)
        {
            host.Logger.Report(ExtensionLogLevel.Warning, "api-13-bridge-unavailable");
            return;
        }

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
        lock (_lifecycleGate)
        {
            _bridge = bridge;
            _registration = context.Registration;
            _settingsStore = settingsStore;
            _apiKeyService = apiKeyService;
            _lifetimeCancellation = new CancellationTokenSource();
            _debounceCancellation = null;
            _driftTimer = null;
            _driftTask = null;
            _lastSelfSettledSettingsVersion = null;
            _lastReconcilerConsumedSettingsVersion = null;
            _lastObservedPublishedConfigurationVersion = null;
            _reconcileUnsettled = false;
            _unsettledDriftTickCount = 0;
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
            ObserveSync);
        var webuiHandler = new WebuiHandler();

        lock (_lifecycleGate)
        {
            _reconciler = reconciler;
            _apiHandler = apiHandler;
        }

        var apiRegistered = context.Registration.TryRegisterStreamingHandler(apiHandler);
        var webuiRegistered = context.Registration.TryRegisterStreamingHandler(webuiHandler);
        if (!apiRegistered || !webuiRegistered)
        {
            if (apiRegistered)
            {
                context.Registration.TryUnregisterHandler(SvchostApiHandler.StableHandlerId);
            }

            if (webuiRegistered)
            {
                context.Registration.TryUnregisterHandler(WebuiHandler.StableHandlerId);
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
            context.Registration.TryUnregisterHandler(SvchostApiHandler.StableHandlerId);
            context.Registration.TryUnregisterHandler(WebuiHandler.StableHandlerId);
            MarkStartupDegraded();
            bridge.Logger.Report(ExtensionLogLevel.Warning, "handler-route-registration-failed");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "handler-route-registration-failed"));
            return;
        }

        var startupDegraded = false;
        if (!host.Events.TrySubscribe(OnHostEventAsync))
        {
            startupDegraded = true;
            MarkStartupDegraded();
            bridge.Logger.Report(ExtensionLogLevel.Warning, "settings-event-subscription-failed");
            bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "settings-event-subscription-failed"));
        }

        bool taskAccepted;
        try
        {
            taskAccepted = await bridge.Tasks.StartAsync("sync", InitialReconcileAsync).ConfigureAwait(false);
        }
        catch (Exception)
        {
            taskAccepted = false;
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
        IExtensionHostBridge13? bridge;
        IExtensionRegistration? registration;
        SvchostApiHandler? apiHandler;
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
            _lifetimeCancellation = null;
            _debounceCancellation = null;
            _driftTimer = null;
            _driftTask = null;
            _lastSelfSettledSettingsVersion = null;
            _lastReconcilerConsumedSettingsVersion = null;
            _lastObservedPublishedConfigurationVersion = null;
            _reconcileUnsettled = false;
            _unsettledDriftTickCount = 0;
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
            registration.TryUnregisterHandler(SvchostApiHandler.StableHandlerId);
            registration.TryUnregisterHandler(WebuiHandler.StableHandlerId);
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
            apiHandler?.Cancel();
            apiHandler?.Dispose();
            debounceCancellation?.Dispose();
            lifetimeCancellation?.Dispose();
        }
    }

    private async ValueTask InitialReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await ReconcileTrackedAsync(
                    Array.Empty<Guid>(),
                    Array.Empty<Guid>(),
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
                FailureCode = SyncErrorCode.ReconcileFailed
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
