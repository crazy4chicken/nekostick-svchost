using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Api;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class SettingsApiTests
{
    private const string TestApiKey = "test-api-key-123456";

    [Fact]
    public async Task Get_returns_empty_registered_groups_when_settings_are_missing()
    {
        using var fixture = await CreateFixtureAsync(initializeSettings: false);

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("GET", string.Empty),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        Assert.Equal("releaseProviders", Assert.Single(document.RootElement.EnumerateObject()).Name);
        Assert.Empty(document.RootElement.GetProperty("releaseProviders").EnumerateObject());
        Assert.Equal(0, fixture.ConfigurationApi.WriteSettingsCallCount);
    }

    [Fact]
    public async Task Get_configs_exposes_service_scope_and_strict_sources()
    {
        using var fixture = await CreateFixtureAsync();
        var setup = await fixture.SettingsStore.UpdateSettingsAsync(settings =>
        {
            settings.Configs["default"] = new SvchostConfigSettings("services: {}");
            settings.Configs["document"] = new SvchostConfigSettings(
                "serviceScope: DoCuMeNt\nstrictSources: true\nservices: {}");
            settings.Configs["invalid"] = new SvchostConfigSettings("services: [");
            return settings;
        });
        Assert.True(setup.IsSuccess);

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("GET", string.Empty, requestPath: "/svchost/api/configs"),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var configs = document.RootElement.EnumerateArray()
            .ToDictionary(config => config.GetProperty("name").GetString()!, StringComparer.Ordinal);

        Assert.Equal("global", configs["default"].GetProperty("serviceScope").GetString());
        Assert.False(configs["default"].GetProperty("strictSources").GetBoolean());
        Assert.Equal("document", configs["document"].GetProperty("serviceScope").GetString());
        Assert.True(configs["document"].GetProperty("strictSources").GetBoolean());
        Assert.Equal(JsonValueKind.Null, configs["invalid"].GetProperty("serviceScope").ValueKind);
        Assert.Equal(JsonValueKind.Null, configs["invalid"].GetProperty("strictSources").ValueKind);
    }

    [Fact]
    public async Task Record_report_updates_config_last_sync_with_failure_details()
    {
        using var fixture = await CreateFixtureAsync();
        var setup = await fixture.SettingsStore.UpdateSettingsAsync(settings =>
        {
            settings.Configs["demo"] = new SvchostConfigSettings("services: {}");
            return settings;
        });
        Assert.True(setup.IsSuccess);

        const string error = "The source could not be resolved.";
        var report = new SyncReport(
            false,
            true,
            DateTimeOffset.UtcNow,
            ImmutableArray.Create(new ServiceSyncReport(
                "demo",
                "api",
                false,
                false,
                null,
                ImmutableArray<Guid>.Empty,
                error,
                SyncErrorCode.SourceFailed)),
            null,
            error)
        {
            FailureCode = SyncErrorCode.SourceFailed
        };
        fixture.Handler.RecordReport(report);

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("GET", string.Empty, requestPath: "/svchost/api/configs"),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var config = Assert.Single(
            document.RootElement.EnumerateArray(),
            item => item.GetProperty("name").GetString() == "demo");
        var lastSync = config.GetProperty("lastSync");
        Assert.False(lastSync.GetProperty("succeeded").GetBoolean());
        Assert.Equal(error, lastSync.GetProperty("error").GetString());
        Assert.Equal("source", lastSync.GetProperty("errorKind").GetString());
    }

    [Fact]
    public async Task Record_run_level_failure_updates_config_last_sync_with_failure_details()
    {
        using var fixture = await CreateFixtureAsync();
        var setup = await fixture.SettingsStore.UpdateSettingsAsync(settings =>
        {
            settings.Configs["demo"] = new SvchostConfigSettings("services: {}");
            return settings;
        });
        Assert.True(setup.IsSuccess);

        const string error = "The reconciliation failed before producing service reports.";
        var report = new SyncReport(
            false,
            true,
            DateTimeOffset.UtcNow,
            ImmutableArray<ServiceSyncReport>.Empty,
            null,
            error)
        {
            FailureCode = SyncErrorCode.ReconcileFailed
        };
        fixture.Handler.RecordReport(report);

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("GET", string.Empty, requestPath: "/svchost/api/configs"),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var config = Assert.Single(
            document.RootElement.EnumerateArray(),
            item => item.GetProperty("name").GetString() == "demo");
        var lastSync = config.GetProperty("lastSync");
        Assert.False(lastSync.GetProperty("succeeded").GetBoolean());
        Assert.Equal(error, lastSync.GetProperty("error").GetString());
        Assert.Equal("reconcile", lastSync.GetProperty("errorKind").GetString());
    }

    [Fact]
    public async Task Newer_config_report_supersedes_run_level_failure()
    {
        using var fixture = await CreateFixtureAsync();
        var setup = await fixture.SettingsStore.UpdateSettingsAsync(settings =>
        {
            settings.Configs["demo"] = new SvchostConfigSettings("services: {}");
            return settings;
        });
        Assert.True(setup.IsSuccess);

        var runFailureCompletedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var configCompletedAt = runFailureCompletedAt.AddSeconds(1);
        fixture.Handler.RecordReport(new SyncReport(
            false,
            true,
            runFailureCompletedAt,
            ImmutableArray<ServiceSyncReport>.Empty,
            null,
            "The reconciliation failed.")
        {
            FailureCode = SyncErrorCode.ReconcileFailed
        });
        fixture.Handler.RecordReport(new SyncReport(
            true,
            true,
            configCompletedAt,
            ImmutableArray.Create(new ServiceSyncReport(
                "demo",
                "api",
                true,
                false,
                null,
                ImmutableArray<Guid>.Empty,
                null)),
            null,
            null));

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("GET", string.Empty, requestPath: "/svchost/api/configs"),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var config = Assert.Single(
            document.RootElement.EnumerateArray(),
            item => item.GetProperty("name").GetString() == "demo");
        var lastSync = config.GetProperty("lastSync");
        Assert.True(lastSync.GetProperty("succeeded").GetBoolean());
        Assert.Equal(configCompletedAt, lastSync.GetProperty("completedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Put_rejects_reserved_global_config_name()
    {
        using var fixture = await CreateFixtureAsync();
        var writesBefore = fixture.ConfigurationApi.WriteSettingsCallCount;

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest(
                "PUT",
                "{\"yaml\":\"services: {}\"}",
                requestPath: "/svchost/api/configs/global"),
            CancellationToken.None);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(writesBefore, fixture.ConfigurationApi.WriteSettingsCallCount);
    }

    [Fact]
    public async Task Service_action_rejects_reserved_global_config_name()
    {
        using var fixture = await CreateFixtureAsync();

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest(
                "POST",
                string.Empty,
                requestPath: "/svchost/api/services/global/api/start"),
            CancellationToken.None);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task Delete_config_cleans_only_its_scoped_artifacts()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "nekostick-svchost-tests",
            Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            using var fixture = await CreateFixtureAsync(dataDirectory: dataDirectory);
            var setup = await fixture.SettingsStore.UpdateSettingsAsync(settings =>
            {
                settings.Configs["global-config"] = new SvchostConfigSettings(
                    "serviceScope: global\nservices:\n  api:\n    source: { path: /tmp/api }\n  own-only:\n    source: { path: /tmp/own-only }",
                    new LockModel
                    {
                        Services = new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal)
                        {
                            ["locked-only"] = new LockServiceEntry()
                        }
                    });
                settings.Configs["global-sibling"] = new SvchostConfigSettings(
                    "serviceScope: global\nservices:\n  api:\n    source: { path: /tmp/sibling-api }\n  sibling:\n    source: { path: /tmp/sibling }");
                settings.Configs["legacy-global"] = new SvchostConfigSettings(
                    "services: [",
                    new LockModel
                    {
                        Services = new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal)
                        {
                            ["legacy-only"] = new LockServiceEntry()
                        }
                    });
                settings.Configs["document-config"] = new SvchostConfigSettings(
                    "serviceScope: document\nservices: {}");
                return settings;
            });
            Assert.True(setup.IsSuccess);

            var artifactsDirectory = Path.Combine(dataDirectory, "svchost", "global", "artifacts");
            async Task CreateArtifactAsync(string serviceName, string content)
            {
                var serviceDirectory = Path.Combine(artifactsDirectory, serviceName);
                Directory.CreateDirectory(serviceDirectory);
                await File.WriteAllTextAsync(Path.Combine(serviceDirectory, "payload"), content);
            }

            await CreateArtifactAsync("api", "sibling-api");
            await CreateArtifactAsync("own-only", "owned");
            await CreateArtifactAsync("locked-only", "locked");
            await CreateArtifactAsync("sibling", "sibling");
            await CreateArtifactAsync("legacy-only", "legacy");
            var documentRoot = Path.Combine(dataDirectory, "svchost", "document-config");
            Directory.CreateDirectory(documentRoot);
            await File.WriteAllTextAsync(Path.Combine(documentRoot, "payload"), "document");

            var globalDelete = await fixture.Handler.HandleStreamingAsync(
                CreateRequest("DELETE", string.Empty, requestPath: "/svchost/api/configs/global-config"),
                CancellationToken.None);
            Assert.Equal(200, globalDelete.StatusCode);
            Assert.False(Directory.Exists(Path.Combine(artifactsDirectory, "own-only")));
            Assert.False(Directory.Exists(Path.Combine(artifactsDirectory, "locked-only")));
            Assert.Equal(
                "sibling-api",
                await File.ReadAllTextAsync(Path.Combine(artifactsDirectory, "api", "payload")));
            Assert.True(File.Exists(Path.Combine(artifactsDirectory, "sibling", "payload")));
            Assert.True(File.Exists(Path.Combine(artifactsDirectory, "legacy-only", "payload")));
            Assert.True(Directory.Exists(Path.Combine(dataDirectory, "svchost", "global")));

            var legacyDelete = await fixture.Handler.HandleStreamingAsync(
                CreateRequest("DELETE", string.Empty, requestPath: "/svchost/api/configs/legacy-global"),
                CancellationToken.None);
            Assert.Equal(200, legacyDelete.StatusCode);
            Assert.False(Directory.Exists(Path.Combine(artifactsDirectory, "legacy-only")));
            Assert.True(File.Exists(Path.Combine(artifactsDirectory, "sibling", "payload")));

            var documentDelete = await fixture.Handler.HandleStreamingAsync(
                CreateRequest("DELETE", string.Empty, requestPath: "/svchost/api/configs/document-config"),
                CancellationToken.None);
            Assert.Equal(200, documentDelete.StatusCode);
            Assert.False(Directory.Exists(documentRoot));
            Assert.True(File.Exists(Path.Combine(artifactsDirectory, "sibling", "payload")));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Put_roundtrips_release_provider_settings_and_preserves_unrelated_settings()
    {
        using var fixture = await CreateFixtureAsync();
        var apiRoute = Guid.CreateVersion7();
        var webuiRoute = Guid.CreateVersion7();
        var setup = await fixture.SettingsStore.UpdateSettingsAsync(settings =>
        {
            settings.Routes = new SvchostRouteSettings(apiRoute, webuiRoute);
            settings.Configs["demo"] = new SvchostConfigSettings("services: {}");
            settings.ReleaseProviders = CreateReleaseProviders("https://old.example/");
            return settings;
        });
        Assert.True(setup.IsSuccess);

        const string payload = "{\"releaseProviders\":{\"github\":{\"mirrors\":[\"https://ghproxy.net/\",\"http://mirror.example/\"]}}}";
        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("PUT", payload),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        using (var document = await ReadJsonAsync(response))
        {
            Assert.Equal(
                new[] { "https://ghproxy.net/", "http://mirror.example/" },
                document.RootElement
                    .GetProperty("releaseProviders")
                    .GetProperty("github")
                    .GetProperty("mirrors")
                    .EnumerateArray()
                    .Select(mirror => mirror.GetString()!));
        }

        var persisted = await fixture.SettingsStore.ReadSettingsAsync();
        Assert.True(persisted.IsSuccess);
        Assert.Equal(TestApiKey, persisted.Value!.Settings!.ApiKey);
        Assert.Equal(apiRoute, persisted.Value.Settings.Routes.Api);
        Assert.Equal(webuiRoute, persisted.Value.Settings.Routes.Webui);
        Assert.Equal("services: {}", persisted.Value.Settings.Configs["demo"].Yaml);
        Assert.Equal(
            new[] { "https://ghproxy.net/", "http://mirror.example/" },
            persisted.Value.Settings.ReleaseProviders!["github"].Mirrors);

        var getResponse = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("GET", string.Empty),
            CancellationToken.None);
        Assert.Equal(200, getResponse.StatusCode);
        using var getDocument = await ReadJsonAsync(getResponse);
        Assert.Equal(
            new[] { "https://ghproxy.net/", "http://mirror.example/" },
            getDocument.RootElement
                .GetProperty("releaseProviders")
                .GetProperty("github")
                .GetProperty("mirrors")
                .EnumerateArray()
                .Select(mirror => mirror.GetString()!));
    }

    [Fact]
    public async Task Put_partial_document_preserves_omitted_settings_groups()
    {
        using var fixture = await CreateFixtureAsync();
        var setup = await fixture.SettingsStore.UpdateSettingsAsync(settings =>
        {
            settings.ReleaseProviders = CreateReleaseProviders("https://existing.example/");
            return settings;
        });
        Assert.True(setup.IsSuccess);

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("PUT", "{}"),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        Assert.Equal(
            "https://existing.example/",
            Assert.Single(
                document.RootElement
                    .GetProperty("releaseProviders")
                    .GetProperty("github")
                    .GetProperty("mirrors")
                    .EnumerateArray())
                .GetString());

        var persisted = await fixture.SettingsStore.ReadSettingsAsync();
        Assert.True(persisted.IsSuccess);
        Assert.Equal(
            "https://existing.example/",
            persisted.Value!.Settings!.ReleaseProviders!["github"].Mirrors!.Single());
    }

    [Fact]
    public async Task Put_rejects_invalid_mirror_urls_with_a_bad_request()
    {
        using var fixture = await CreateFixtureAsync();
        var writesBefore = fixture.ConfigurationApi.WriteSettingsCallCount;

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("PUT", "{\"releaseProviders\":{\"github\":{\"mirrors\":[\"ftp://mirror.example/\"]}}}"),
            CancellationToken.None);

        Assert.Equal(400, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var message = document.RootElement.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains("releaseProviders.github.mirrors[0]", message, StringComparison.Ordinal);
        Assert.Equal(writesBefore, fixture.ConfigurationApi.WriteSettingsCallCount);
    }

    [Fact]
    public async Task Put_rejects_unknown_settings_groups_with_a_bad_request()
    {
        using var fixture = await CreateFixtureAsync();
        var writesBefore = fixture.ConfigurationApi.WriteSettingsCallCount;

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("PUT", "{\"futureGroup\":{}}"),
            CancellationToken.None);

        Assert.Equal(400, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        Assert.Contains(
            "futureGroup",
            document.RootElement.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(writesBefore, fixture.ConfigurationApi.WriteSettingsCallCount);
    }

    [Fact]
    public async Task Settings_endpoints_require_the_api_key()
    {
        using var fixture = await CreateFixtureAsync();
        var writesBefore = fixture.ConfigurationApi.WriteSettingsCallCount;

        var getResponse = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("GET", string.Empty, apiKey: null),
            CancellationToken.None);
        var putResponse = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("PUT", "{}", apiKey: null),
            CancellationToken.None);

        Assert.Equal(401, getResponse.StatusCode);
        Assert.Equal(401, putResponse.StatusCode);
        Assert.Equal(writesBefore, fixture.ConfigurationApi.WriteSettingsCallCount);
    }

    [Theory]
    [InlineData(ConfigurationErrorCode.Validation, 422, "validation")]
    [InlineData(ConfigurationErrorCode.ConcurrencyConflict, 409, "conflict")]
    [InlineData(ConfigurationErrorCode.NotFound, 404, "not_found")]
    [InlineData(ConfigurationErrorCode.Unsupported, 403, "forbidden")]
    [InlineData(ConfigurationErrorCode.StorageUnavailable, 502, "backend_unavailable")]
    public async Task Put_preserves_host_failure_reason_and_stable_error_shape(
        ConfigurationErrorCode failureCode,
        int statusCode,
        string localCode)
    {
        using var fixture = await CreateFixtureAsync();
        const string reason = "The host rejected this settings operation: exact detail.";
        fixture.ConfigurationApi.WriteFailure = ConfigurationWriteResult.Failure(
            new ConfigurationError(failureCode, reason));

        var response = await fixture.Handler.HandleStreamingAsync(
            CreateRequest("PUT", "{\"releaseProviders\":{}}"),
            CancellationToken.None);

        Assert.Equal(statusCode, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var error = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("error", error.Name);
        Assert.Equal(new[] { "code", "message" }, error.Value.EnumerateObject().Select(property => property.Name));
        Assert.Equal(localCode, error.Value.GetProperty("code").GetString());
        Assert.Equal(reason, error.Value.GetProperty("message").GetString());
    }

    private static Dictionary<string, ReleaseProviderSettings> CreateReleaseProviders(params string[] mirrors) =>
        new(StringComparer.Ordinal)
        {
            ["github"] = new ReleaseProviderSettings { Mirrors = new List<string>(mirrors) }
        };

    private static ExtensionStreamingRequest CreateRequest(
        string method,
        string body,
        string? apiKey = TestApiKey,
        string requestPath = "/svchost/api/settings")
    {
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = apiKey is null
            ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()
            : new[] { new KeyValuePair<string, IEnumerable<string>>("X-Api-Key", new[] { apiKey }) };

        return new ExtensionStreamingRequest(
            method,
            requestPath,
            headers,
            new MemoryStream(Encoding.UTF8.GetBytes(body), writable: false));
    }

    private static async Task<JsonDocument> ReadJsonAsync(ExtensionStreamingResponse response) =>
        await JsonDocument.ParseAsync(response.BodyStream);

    private static async Task<SettingsApiFixture> CreateFixtureAsync(
        bool initializeSettings = true,
        string? dataDirectory = null)
    {
        var configurationApi = new FakeConfigurationApi();
        var settingsStore = new SettingsStore(configurationApi);
        var bridge = new FakeBridge { DataDirectory = dataDirectory ?? string.Empty };
        var apiKeyService = new ApiKeyService(settingsStore, bridge);
        if (initializeSettings)
        {
            var initialization = await apiKeyService.InitializeAsync();
            Assert.True(initialization.Succeeded);
            var keyWrite = await apiKeyService.SetPermanentKeyAsync(TestApiKey);
            Assert.True(keyWrite.IsSuccess);
        }
        else
        {
            apiKeyService.ReloadFromSettings(new SvchostSettings(
                TestApiKey,
                new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7())));
        }

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
        return new SettingsApiFixture(handler, settingsStore, configurationApi);
    }

    private static HostConfigurationSnapshot CreateHostConfigurationSnapshot() =>
        new(
            0,
            new GlobalSettingsConfiguration(),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

    private sealed class SettingsApiFixture : IDisposable
    {
        public SettingsApiFixture(
            SvchostApiHandler handler,
            SettingsStore settingsStore,
            FakeConfigurationApi configurationApi)
        {
            Handler = handler;
            SettingsStore = settingsStore;
            ConfigurationApi = configurationApi;
        }

        public SvchostApiHandler Handler { get; }

        public SettingsStore SettingsStore { get; }

        public FakeConfigurationApi ConfigurationApi { get; }

        public void Dispose() => Handler.Dispose();
    }
}
