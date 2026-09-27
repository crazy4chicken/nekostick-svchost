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

    public HostApiVersion ApiVersion => default;

    public ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ConfigurationReadResult<ExtensionConfigurationSnapshot>.Success(null!));

    public ValueTask<ConfigurationWriteResult> ApplyAsync(
        long expectedVersion,
        ExtensionConfigurationChangeSet changes,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ConfigurationWriteResult.Success(expectedVersion + 1));

    public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadSettingsAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(_settings is null
            // The real host reports NoSettings (NotFound before API 1.4) when the extension has
            // no settings row yet.
            ? ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(MissingSettingsErrorCode))
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

internal sealed class FakeBridge : IExtensionHostBridge13
{
    public FakeBridge()
    {
        LogWriterSink = new FakeLogWriter();
        LoggerSink = new FakeLogger();
        StatusSink = new FakeStatusSink();
    }

    public FakeLogWriter LogWriterSink { get; }

    public FakeLogger LoggerSink { get; }

    public FakeStatusSink StatusSink { get; }

    public IExtensionSupervisorApi Supervisor => null!;
    public IExtensionRouteEvents RouteEvents => null!;
    IExtensionLogWriter IExtensionHostBridge13.LogWriter => LogWriterSink;
    public IExtensionManagementApi Management => null!;
    public HostApiVersion ApiVersion => default;
    public IExtensionSettingsReader Configuration => null!;
    public IExtensionConfigurationApi ConfigurationApi => null!;
    public IExtensionFullConfigurationApi FullConfiguration => null!;
    public IExtensionRouteApi Routes => null!;
    public IExtensionServiceApi Services => null!;
    public IExtensionEndpointApi Endpoints => null!;
    public IExtensionLifecycleApi Lifecycle => null!;
    public IExtensionContractRegistry Contracts => null!;
    public IExtensionTaskScheduler Tasks => null!;
    public IExtensionEventPublisher Events => null!;
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
