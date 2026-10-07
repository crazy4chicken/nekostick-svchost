using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Api;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Logs;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class ServiceLogsApiTests
{
    private const string TestApiKey = "service-logs-api-key-123456";

    [Fact]
    public async Task Get_returns_the_requested_log_file_page()
    {
        var dataDirectory = CreateTempDirectory();
        try
        {
            using var fixture = await CreateFixtureAsync(dataDirectory);
            await AddServiceConfigAsync(fixture, "demo", "api", Guid.NewGuid());
            var logsDirectory = Path.Combine(dataDirectory, "svchost", "demo", "logs");
            Directory.CreateDirectory(logsDirectory);
            File.WriteAllLines(Path.Combine(logsDirectory, "api.log"), new[] { "current-1", "current-2" });
            File.WriteAllLines(Path.Combine(logsDirectory, "api.1.log"), new[] { "archive-1", "archive-2" });

            var response = await fixture.Handler.HandleStreamingAsync(
                CreateRequest("/svchost/api/configs/demo/services/api/logs?file=1"),
                CancellationToken.None);

            Assert.Equal(200, response.StatusCode);
            using var document = await JsonDocument.ParseAsync(response.BodyStream);
            var page = document.RootElement;
            Assert.Equal("api", page.GetProperty("service").GetString());
            Assert.Equal(1, page.GetProperty("file").GetInt32());
            Assert.Equal(2, page.GetProperty("fileCount").GetInt32());
            Assert.Equal(2, page.GetProperty("lineCount").GetInt32());
            Assert.Equal(
                new[] { "archive-1", "archive-2" },
                page.GetProperty("lines").EnumerateArray().Select(line => line.GetString()).ToArray());
        }
        finally
        {
            DeleteTempDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Get_returns_not_found_for_unknown_config_or_service()
    {
        var dataDirectory = CreateTempDirectory();
        try
        {
            using var fixture = await CreateFixtureAsync(dataDirectory);
            await AddServiceConfigAsync(fixture, "demo", "api", Guid.NewGuid());

            var unknownConfig = await fixture.Handler.HandleStreamingAsync(
                CreateRequest("/svchost/api/configs/missing/services/api/logs"),
                CancellationToken.None);
            var unknownService = await fixture.Handler.HandleStreamingAsync(
                CreateRequest("/svchost/api/configs/demo/services/missing/logs"),
                CancellationToken.None);

            Assert.Equal(404, unknownConfig.StatusCode);
            Assert.Equal(404, unknownService.StatusCode);
        }
        finally
        {
            DeleteTempDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Get_returns_not_found_when_file_index_is_out_of_range()
    {
        var dataDirectory = CreateTempDirectory();
        try
        {
            using var fixture = await CreateFixtureAsync(dataDirectory);
            await AddServiceConfigAsync(fixture, "demo", "api", Guid.NewGuid());
            var logsDirectory = Path.Combine(dataDirectory, "svchost", "demo", "logs");
            Directory.CreateDirectory(logsDirectory);
            File.WriteAllText(Path.Combine(logsDirectory, "api.log"), "current\n");

            var response = await fixture.Handler.HandleStreamingAsync(
                CreateRequest("/svchost/api/configs/demo/services/api/logs?file=5"),
                CancellationToken.None);

            Assert.Equal(404, response.StatusCode);
        }
        finally
        {
            DeleteTempDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Tail_replays_current_file_lines_from_the_requested_index()
    {
        var dataDirectory = CreateTempDirectory();
        try
        {
            using var fixture = await CreateFixtureAsync(dataDirectory);
            await AddServiceConfigAsync(fixture, "demo", "api", Guid.NewGuid());
            var logsDirectory = Path.Combine(dataDirectory, "svchost", "demo", "logs");
            Directory.CreateDirectory(logsDirectory);
            File.WriteAllLines(Path.Combine(logsDirectory, "api.log"), new[] { "first", "second \"line\"" });

            var response = await fixture.Handler.HandleStreamingAsync(
                CreateRequest("/svchost/api/configs/demo/services/api/logs/tail?fromLine=1"),
                CancellationToken.None);

            Assert.Equal(200, response.StatusCode);
            using var reader = new StreamReader(response.BodyStream, Encoding.UTF8);
            var frame = await reader.ReadToEndAsync();
            Assert.StartsWith("data: ", frame);
            Assert.EndsWith("\n\n", frame);
            using var payload = JsonDocument.Parse(frame.Substring("data: ".Length, frame.Length - "data: ".Length - 2));
            Assert.Equal("second \"line\"", payload.RootElement.GetProperty("line").GetString());
        }
        finally
        {
            DeleteTempDirectory(dataDirectory);
        }
    }

    [Fact]
    public void Recorder_disables_unsupported_capture_once_and_preserves_host_detail()
    {
        var output = new FakeServiceOutputApi { FailureMessage = "The host log capture executor is disabled." };
        var logger = new FakeLogger();
        var writer = new FakeLogWriter();
        var target = new ServiceLogTarget(Guid.CreateVersion7(), "api", "unused-log-directory");
        using var recorder = new ServiceLogRecorder(output, logger, writer);

        recorder.SyncTargets([target]);
        recorder.SyncTargets([target]);

        Assert.Single(output.Cursors);
        Assert.Equal("service-output-unsupported", Assert.Single(logger.Entries).Code);
        Assert.Contains($"Unsupported: {output.FailureMessage}", Assert.Single(writer.Entries).Message, StringComparison.Ordinal);
        Assert.Null(recorder.SubscribeLiveLines(target.ServiceId, _ => { }));
    }

    [Fact]
    public void Recorder_retries_invalid_cursor_from_latest_and_releases_subscription()
    {
        var root = CreateTempDirectory();
        try
        {
            var output = new FakeServiceOutputApi { FailureMessage = "The cursor belongs to the previous host session." };
            output.Results.Enqueue(ExtensionServiceLogCode.Subscribed);
            output.Results.Enqueue(ExtensionServiceLogCode.InvalidCursor);
            output.Results.Enqueue(ExtensionServiceLogCode.Subscribed);
            var writer = new FakeLogWriter();
            var target = new ServiceLogTarget(Guid.CreateVersion7(), "api", root);
            using (var recorder = new ServiceLogRecorder(output, new FakeLogger(), writer))
            {
                recorder.SyncTargets([target]);
                output.Sink!.OnEntry(new ExtensionServiceLogEntry(
                    ExtensionServiceLogEntryKind.GenerationStarted,
                    target.ServiceId,
                    5,
                    DateTimeOffset.UtcNow,
                    processInstanceId: Guid.NewGuid(),
                    attemptNumber: 1));
                output.Sink.OnCompleted();
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    recorder.SyncTargets([target]);
                    return output.Cursors.Count == 3;
                }, TimeSpan.FromSeconds(5)));

                Assert.Equal(new long?[] { null, 5, null }, output.Cursors);
                Assert.Equal(2, output.Subscriptions.Count);
                Assert.True(output.Subscriptions[0].Disposed);
                Assert.False(output.Subscriptions[1].Disposed);
                Assert.Contains(writer.Entries, item => item.Message.Contains($"InvalidCursor: {output.FailureMessage}", StringComparison.Ordinal));

                recorder.SyncTargets([]);
                Assert.All(output.Subscriptions, subscription => Assert.True(subscription.Disposed));
            }
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static ExtensionStreamingRequest CreateRequest(string requestPath) =>
        new(
            "GET",
            requestPath,
            new[]
            {
                new KeyValuePair<string, IEnumerable<string>>(
                    "X-Api-Key",
                    new[] { TestApiKey })
            },
            new MemoryStream(Array.Empty<byte>(), writable: false));

    private static async Task AddServiceConfigAsync(
        LogsApiFixture fixture,
        string configName,
        string serviceName,
        Guid serviceId)
    {
        var lockModel = new LockModel();
        lockModel.Services[serviceName] = new LockServiceEntry(new LockSource(), serviceId);
        var result = await fixture.SettingsStore.UpdateSettingsAsync(settings =>
        {
            settings.Configs[configName] = new SvchostConfigSettings(
                "serviceScope: document\nservices: {}",
                lockModel);
            return settings;
        });
        Assert.True(result.IsSuccess);
    }

    private static async Task<LogsApiFixture> CreateFixtureAsync(string dataDirectory)
    {
        var configurationApi = new FakeConfigurationApi();
        var settingsStore = new SettingsStore(configurationApi);
        var initializationFullConfiguration = new FakeFullConfigurationApi(CreateHostConfigurationSnapshot());
        var bridge = new FakeBridge { DataDirectory = dataDirectory, FullConfiguration = initializationFullConfiguration };
        var apiKeyService = new ApiKeyService(settingsStore, bridge);
        var initialization = await apiKeyService.InitializeAsync();
        Assert.True(initialization.Succeeded);
        var keyWrite = await apiKeyService.SetPermanentKeyAsync(TestApiKey);
        Assert.True(keyWrite.IsSuccess);

        var composeParser = new ComposeFileParser();
        var reconciler = new Reconciler(
            settingsStore,
            composeParser,
            new SourceResolver(),
            new FakeFullConfigurationApi(CreateHostConfigurationSnapshot()));
        var handler = new SvchostApiHandler(
            apiKeyService,
            settingsStore,
            composeParser,
            reconciler,
            bridge);
        return new LogsApiFixture(handler, settingsStore);
    }

    private static HostConfigurationSnapshot CreateHostConfigurationSnapshot() =>
        new(
            0,
            new GlobalSettingsConfiguration(),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nekostick-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class LogsApiFixture : IDisposable
    {
        public LogsApiFixture(SvchostApiHandler handler, SettingsStore settingsStore)
        {
            Handler = handler;
            SettingsStore = settingsStore;
        }

        public SvchostApiHandler Handler { get; }

        public SettingsStore SettingsStore { get; }

        public void Dispose() => Handler.Dispose();
    }
}
