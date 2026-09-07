using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.ServiceHost.Api;
using Nekolla.Nekostick.ServiceHost.Compose;
using Nekolla.Nekostick.ServiceHost.Settings;
using Nekolla.Nekostick.ServiceHost.Sync;
using Nekolla.Nekostick.ServiceHost.Webui;

namespace Nekolla.Nekostick.ServiceHost;

/// <summary>Entrypoint for the svchost management API and WebUI extension.</summary>
public sealed partial class SvchostEntry : IExtensionEntry
{
    private static readonly Task CompletedTask = Task.CompletedTask;
    private readonly object _lifecycleGate = new();
    private readonly object _debounceGate = new();
    private readonly object _reconcileGate = new();

    private IExtensionHostBridge13? _bridge;
    private IExtensionRegistration? _registration;
    private SettingsStore? _settingsStore;
    private Reconciler? _reconciler;
    private SvchostApiHandler? _apiHandler;
    private CancellationTokenSource? _lifetimeCancellation;
    private CancellationTokenSource? _debounceCancellation;
    private long? _lastSelfSettledSettingsVersion;
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
        if (!ExtensionAbi.IsApi13Supported(host.ApiVersion))
        {
            host.Logger.Report(ExtensionLogLevel.Warning, "api-13-unsupported");
            return;
        }

        if (host is not IExtensionHostBridge13 bridge)
        {
            host.Logger.Report(ExtensionLogLevel.Warning, "api-13-bridge-unavailable");
            return;
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
            _lifetimeCancellation = new CancellationTokenSource();
            _debounceCancellation = null;
            _lastSelfSettledSettingsVersion = null;
            _startupDegraded = false;
            _reconcileIdle = CreateCompletedSource();
            _activeReconciles = 0;
        }

        if (!initialization.Succeeded)
        {
            MarkStartupDegraded();
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


        var composeParser = new ComposeFileParser();
        var reconciler = new Reconciler(
            settingsStore,
            composeParser,
            new SourceResolver(),
            bridge.FullConfiguration,
            bridge.DataDirectory);
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
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        IExtensionHostBridge13? bridge;
        IExtensionRegistration? registration;
        SvchostApiHandler? apiHandler;
        CancellationTokenSource? lifetimeCancellation;
        CancellationTokenSource? debounceCancellation;
        Task idleTask;

        lock (_lifecycleGate)
        {
            bridge = _bridge;
            registration = _registration;
            apiHandler = _apiHandler;
            lifetimeCancellation = _lifetimeCancellation;
            idleTask = GetReconcileIdleTask();
            debounceCancellation = _debounceCancellation;
            _bridge = null;
            _registration = null;
            _settingsStore = null;
            _reconciler = null;
            _apiHandler = null;
            _lifetimeCancellation = null;
            _debounceCancellation = null;
            _lastSelfSettledSettingsVersion = null;
        } 
        if (bridge is null)
        {
            return;
        }

        try
        {
            lifetimeCancellation?.Cancel();
            debounceCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrently completing debounce may already have disposed its source.
        }
        apiHandler?.Cancel();

        if (registration is not null)
        {
            registration.TryUnregisterHandler(SvchostApiHandler.StableHandlerId);
            registration.TryUnregisterHandler(WebuiHandler.StableHandlerId);
        }

        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            await Task.WhenAny(idleTask, timeoutTask).ConfigureAwait(false);
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
            ObserveSync(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The Host task scheduler cancels this callback during extension stop.
        }
        catch (Exception)
        {
            _bridge?.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, "initial-sync-failed"));
        }
    }

    private static TaskCompletionSource<bool> CreateCompletedSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult(true);
        return source;
    }
}
