using System.Collections.Immutable;
using System.Reflection;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.UnitTests;

internal sealed class FakeConfigurationApi : IExtensionConfigurationApi
{
    private ExtensionSettingsConfiguration? _settings;
    private long _settingsVersion;

    public FakeConfigurationApi(ExtensionSettingsConfiguration? settings = null)
    {
        _settings = settings;
        _settingsVersion = settings?.Version ?? 0;
    }

    /// <summary>Gets or sets the error code reported when no settings row exists.</summary>
    public ConfigurationErrorCode MissingSettingsErrorCode { get; set; } = ConfigurationErrorCode.NoSettings;

    public int WriteSettingsCallCount { get; private set; }

    public List<ExtensionSettingsConfiguration> WrittenSettings { get; } = new();

    public ConfigurationWriteResult? NextWriteResult { get; set; }

    public ConfigurationWriteResult? WriteFailure { get; set; }

    public HostApiVersion ApiVersion => new(1, 4, 0);

    public ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ConfigurationReadResult<ExtensionConfigurationSnapshot>.Success(
            new ExtensionConfigurationSnapshot(
                _settingsVersion,
                ImmutableArray<ExtensionRouteConfiguration>.Empty,
                ImmutableArray<ExtensionServiceConfiguration>.Empty,
                _settings)));

    public ValueTask<ConfigurationWriteResult> ApplyAsync(
        long expectedVersion,
        ExtensionConfigurationChangeSet changes,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ConfigurationWriteResult.Success(expectedVersion + 1));

    public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadSettingsAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(_settings is null
            ? ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(MissingSettingsErrorCode, "The extension has no persisted settings document."))
            : ConfigurationReadResult<ExtensionSettingsConfiguration>.Success(_settings));

    public ValueTask<ConfigurationWriteResult> WriteSettingsAsync(
        long expectedVersion,
        ExtensionSettingsConfiguration settings,
        CancellationToken cancellationToken)
    {
        WriteSettingsCallCount++;
        WrittenSettings.Add(settings);
        if (NextWriteResult is not null)
        {
            var result = NextWriteResult;
            NextWriteResult = null;
            return ValueTask.FromResult(result);
        }

        if (WriteFailure is not null)
        {
            return ValueTask.FromResult(WriteFailure);
        }

        _settingsVersion = expectedVersion + 1;
        _settings = new ExtensionSettingsConfiguration(
            settings.ExtensionId,
            settings.SchemaVersion,
            settings.SettingsJson,
            _settingsVersion);
        return ValueTask.FromResult(ConfigurationWriteResult.Success(_settingsVersion));
    }
}

internal sealed class FakeLogWriter : IExtensionLogWriter
{
    public List<(ExtensionLogLevel Level, string Message)> Entries { get; } = new();

    public void WriteText(ExtensionLogLevel level, string message) => Entries.Add((level, message));
}

internal sealed class FakeLogger : IExtensionLogger
{
    public List<(ExtensionLogLevel Level, string Code)> Entries { get; } = new();

    public void Report(ExtensionLogLevel level, string code) => Entries.Add((level, code));
}

internal sealed class FakeStatusSink : IExtensionStatusSink
{
    public List<ExtensionStatus> Entries { get; } = new();

    public void Report(ExtensionStatus status) => Entries.Add(status);
}

internal sealed class FakeBridge : IExtensionHostBridge14
{
    public FakeBridge()
    {
        LogWriterSink = new FakeLogWriter();
        LoggerSink = new FakeLogger();
        StatusSink = new FakeStatusSink();
    }

    public string DataDirectory { get; set; } = string.Empty;

    public FakeLogWriter LogWriterSink { get; }

    public FakeLogger LoggerSink { get; }

    public FakeStatusSink StatusSink { get; }

    public IExtensionSupervisorApi? SupervisorApi { get; set; }
    public IExtensionSupervisorApi Supervisor => SupervisorApi!;
    public IExtensionRouteEvents RouteEvents => null!;
    IExtensionLogWriter IExtensionHostBridge13.LogWriter => LogWriterSink;
    public IExtensionManagementApi Management => null!;
    public IExtensionDependencyApi Dependencies => null!;
    public IExtensionServiceOutputApi ServiceOutput { get; set; } = new FakeServiceOutputApi();
    public IExtensionServiceRuntimeStateApi ServiceRuntimeState => null!;
    public HostApiVersion ApiVersion { get; set; } = new(1, 4, 0);
    public IExtensionSettingsReader Configuration => null!;
    public IExtensionConfigurationApi ConfigurationApi { get; set; } = new FakeConfigurationApi();
    public IExtensionFullConfigurationApi FullConfiguration { get; set; } = new FakeFullConfigurationApi(new(
        0,
        new GlobalSettingsConfiguration(),
        ImmutableArray<RouteConfiguration>.Empty,
        ImmutableArray<ServiceConfiguration>.Empty,
        ImmutableArray<ExtensionRecordConfiguration>.Empty,
        ImmutableArray<ExtensionSettingsConfiguration>.Empty));
    public IExtensionRouteApi Routes { get; set; } = new FakeRouteApi();
    public IExtensionServiceApi Services => null!;
    public IExtensionEndpointApi Endpoints => null!;
    public IExtensionLifecycleApi Lifecycle => null!;
    public IExtensionContractRegistry Contracts => null!;
    public FakeTaskScheduler TaskScheduler { get; } = new();
    public IExtensionTaskScheduler Tasks => TaskScheduler;
    public FakeEventPublisher EventPublisher { get; } = new();
    public IExtensionEventPublisher Events => EventPublisher;
    public IExtensionStatusSink Status => StatusSink;
    public IExtensionLogger Logger => LoggerSink;
}

internal sealed class FakeFullConfigurationApi : IExtensionFullConfigurationApi
{
    public FakeFullConfigurationApi(HostConfigurationSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public HostConfigurationSnapshot Snapshot { get; set; }

    public ConfigurationReadResult<HostConfigurationSnapshot>? NextReadResult { get; set; }

    public int ReplaceCallCount { get; private set; }

    public long LastExpectedVersion { get; private set; }

    public ConfigurationChangeSet? LastChanges { get; private set; }

    public List<ConfigurationChangeSet> ChangesHistory { get; } = [];

    public ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadAsync(
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(
            NextReadResult ?? ConfigurationReadResult<HostConfigurationSnapshot>.Success(Snapshot));
    }

    public ValueTask<ConfigurationWriteResult> ReplaceAsync(
        long expectedVersion,
        ConfigurationChangeSet changes,
        CancellationToken cancellationToken)
    {
        ReplaceCallCount++;
        LastExpectedVersion = expectedVersion;
        LastChanges = changes;
        ChangesHistory.Add(changes);
        Snapshot = new HostConfigurationSnapshot(
            expectedVersion + 1,
            changes.GlobalSettings,
            changes.Routes,
            changes.Services,
            changes.ExtensionRecords,
            changes.ExtensionSettings);
        return ValueTask.FromResult(ConfigurationWriteResult.Success(expectedVersion + 1));
    }
}

public class CountingSupervisorProxy : DispatchProxy
{
    public int ReadCallCount { get; private set; }

    public int ResumeCallCount { get; private set; }

    internal static IExtensionSupervisorApi Create(out CountingSupervisorProxy fake)
    {
        var supervisor = DispatchProxy.Create<IExtensionSupervisorApi, CountingSupervisorProxy>();
        fake = (CountingSupervisorProxy)(object)supervisor;
        return supervisor;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(IExtensionSupervisorApi.ReadAsync):
                ReadCallCount++;
                break;
            case nameof(IExtensionSupervisorApi.ResumeAsync):
                ResumeCallCount++;
                break;
        }

        throw new NotSupportedException($"Unexpected supervisor call: {targetMethod?.Name}.");
    }
}

internal sealed class FakeStartContext(IExtensionHostBridge host, FakeRegistration registration) : IExtensionStartContext
{
    public bool Reloading => false;
    public IExtensionContractRegistry Contracts => host.Contracts;
    public IExtensionHostBridge Host => host;
    public IExtensionRegistration Registration => registration;
}

internal sealed class FakeRegistration : IExtensionRegistration
{
    public Dictionary<string, ExtensionRegistrationResult> RegistrationResults { get; } = new();
    public ExtensionRegistrationResult UnregistrationResult { get; set; } = ExtensionRegistrationResult.Success;
    public List<string> RegisteredHandlers { get; } = [];
    public List<string> UnregisteredHandlers { get; } = [];

    public ExtensionRegistrationResult TryRegisterHandler(IExtensionHandler handler) => Register(handler.HandlerId);
    public ExtensionRegistrationResult TryRegisterStreamingHandler(IExtensionStreamingHandler handler) => Register(handler.HandlerId);
    public ExtensionRegistrationResult TryRegisterFallback(IExtensionFallback fallback) => ExtensionRegistrationResult.Success;
    public ExtensionRegistrationResult TryUnregisterFallback() => ExtensionRegistrationResult.Success;

    public ExtensionRegistrationResult TryUnregisterHandler(string handlerId)
    {
        UnregisteredHandlers.Add(handlerId);
        return UnregistrationResult;
    }

    private ExtensionRegistrationResult Register(string handlerId)
    {
        var result = RegistrationResults.GetValueOrDefault(handlerId, ExtensionRegistrationResult.Success);
        if (result.Succeeded)
        {
            RegisteredHandlers.Add(handlerId);
        }

        return result;
    }
}

internal sealed class FakeTaskScheduler : IExtensionTaskScheduler
{
    public ExtensionTaskStartResult Result { get; set; } = ExtensionTaskStartResult.Success;
    public Exception? Exception { get; set; }
    public List<string> TaskNames { get; } = [];

    public ValueTask<ExtensionTaskStartResult> StartAsync(string taskName, Func<CancellationToken, ValueTask> callback)
    {
        TaskNames.Add(taskName);
        if (Exception is not null)
        {
            throw Exception;
        }

        return ValueTask.FromResult(Result);
    }
}

internal sealed class FakeEventPublisher : IExtensionEventPublisher
{
    public ExtensionEventSubscribeResult SubscribeResult { get; set; } = ExtensionEventSubscribeResult.Success;
    public ExtensionEventPublishResult TryPublish(ExtensionEvent @event) => ExtensionEventPublishResult.Success;
    public ExtensionEventSubscribeResult TrySubscribe(Func<ExtensionEvent, CancellationToken, ValueTask> callback) => SubscribeResult;
}

internal sealed class FakeRouteApi : IExtensionRouteApi
{
    public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>> ReadOwnedAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>.Success([]));

    public ValueTask<ConfigurationWriteResult> UpsertAsync(
        long expectedVersion,
        ExtensionRouteConfiguration route,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ConfigurationWriteResult.Success(expectedVersion + 1));

    public ValueTask<ConfigurationWriteResult> RemoveAsync(
        long expectedVersion,
        Guid routeId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ConfigurationWriteResult.Success(expectedVersion + 1));
}

internal sealed class FakeServiceOutputApi : IExtensionServiceOutputApi
{
    public Queue<ExtensionServiceLogCode> Results { get; } = new();
    public string FailureMessage { get; set; } = "Service log capture is unavailable on this host.";
    public List<long?> Cursors { get; } = [];
    public List<FakeServiceLogSubscription> Subscriptions { get; } = [];
    public IExtensionServiceLogSink? Sink { get; private set; }

    public ValueTask<ExtensionServiceOutputStreamResult> OpenStreamAsync(
        Guid serviceId,
        ExtensionServiceOutputStream stream,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ExtensionServiceOutputStreamResult(
            false, ExtensionServiceOutputCode.Unsupported, serviceId, null, new ExtensionErrorDetail(FailureMessage)));

    public ValueTask<ExtensionServiceLogSubscriptionResult> SubscribeAsync(
        Guid serviceId,
        IExtensionServiceLogSink sink,
        long? sinceSequence = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Cursors.Add(sinceSequence);
        Sink = sink;
        var code = Results.Count == 0 ? ExtensionServiceLogCode.Unsupported : Results.Dequeue();
        FakeServiceLogSubscription? subscription = null;
        if (code == ExtensionServiceLogCode.Subscribed)
        {
            subscription = new FakeServiceLogSubscription();
            Subscriptions.Add(subscription);
        }

        return ValueTask.FromResult(new ExtensionServiceLogSubscriptionResult(
            subscription is not null,
            code,
            serviceId,
            subscription,
            subscription is null ? new ExtensionErrorDetail(FailureMessage) : null));
    }
}

internal sealed class FakeServiceLogSubscription : IExtensionServiceLogSubscription
{
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public class Bridge13OnlyProxy : DispatchProxy
{
    internal FakeLogger LoggerSink { get; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_ApiVersion" => new HostApiVersion(1, 4, 0),
        "get_Logger" => LoggerSink,
        _ => throw new NotSupportedException($"Unexpected bridge call: {targetMethod?.Name}.")
    };
}
