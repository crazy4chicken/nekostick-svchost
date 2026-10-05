using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class SettingsModelTests
{
    [Fact]
    public async Task SettingsStore_rejects_persisted_schema_version_other_than_one()
    {
        var settings = CreateSettings();
        var rawJson = "{\"apiKey\":null,\"routes\":{\"api\":\"" + settings.Routes.Api + "\",\"webui\":\"" + settings.Routes.Webui + "\"},\"configs\":{}}";
        var persisted = new ExtensionSettingsConfiguration(
            SvchostSettingsSchema.ExtensionId,
            schemaVersion: 2,
            rawJson,
            version: 7);
        var store = new SettingsStore(new FakeConfigurationApi(persisted));

        var result = await store.ReadSettingsAsync();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Code == Nekolla.Nekostick.Contracts.ConfigurationErrorCode.Validation);
        Assert.Equal("The persisted settings schema is unsupported.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task SettingsStore_treats_missing_settings_row_as_empty_document()
    {
        // NoSettings represents the fresh-install state; the initial write creates the document.
        var store = new SettingsStore(new FakeConfigurationApi());

        var result = await store.ReadSettingsAsync();

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Null(result.Value.Settings);
        Assert.Equal(0, result.Value.Version);
    }

    [Fact]
    public async Task SettingsStore_does_not_treat_not_found_as_missing_settings()
    {
        var store = new SettingsStore(
            new FakeConfigurationApi { MissingSettingsErrorCode = ConfigurationErrorCode.NotFound });

        var result = await store.ReadSettingsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.NotFound, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task SettingsStore_preserves_raw_json_validation_reason_without_writing()
    {
        var configurationApi = new FakeConfigurationApi();
        var store = new SettingsStore(configurationApi);

        var result = await store.WriteRawSettingsAsync(0, "[]");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ConfigurationErrorCode.Validation, error.Code);
        Assert.Equal("The settings document must be an object.", error.Message);
        Assert.Equal(0, configurationApi.WriteSettingsCallCount);
    }

    [Fact]
    public async Task SettingsStore_preserves_update_failure_reason_without_writing()
    {
        var configurationApi = new FakeConfigurationApi();
        var store = new SettingsStore(configurationApi);
        const string reason = "The selected configuration was removed.";

        var result = await store.UpdateSettingsAsync(_ => throw new InvalidOperationException(reason));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ConfigurationErrorCode.Validation, error.Code);
        Assert.Equal(reason, error.Message);
        Assert.Equal(0, configurationApi.WriteSettingsCallCount);
    }

    [Fact]
    public void Validate_rejects_invalid_stopped_service_names()
    {
        var settings = CreateSettings();
        settings.Configs["demo"] = new SvchostConfigSettings(
            "services: {}",
            stopped: ["api", "Not_A_Service"]);

        var errors = settings.Validate();

        Assert.Contains(errors, error => error.Contains("stopped contains an invalid service name", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_config_names_outside_name_pattern()
    {
        var settings = CreateSettings();
        settings.Configs["bad_name"] = new SvchostConfigSettings("services: {}");

        var errors = settings.Validate();

        Assert.Contains(errors, error => error.Contains("configs.bad_name has an invalid name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("global")]
    [InlineData("GLOBAL")]
    public void Validate_rejects_reserved_global_config_names(string name)
    {
        var settings = CreateSettings();
        settings.Configs[name] = new SvchostConfigSettings("services: {}");

        var errors = settings.Validate();

        Assert.Contains(errors, error => error.Contains($"configs.{name} has an invalid name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettingsStore_roundtrips_settings_with_null_api_key()
    {
        var settings = CreateSettings();
        settings.Observability.LogLevel = "Warning";
        settings.ReleaseProviders = new Dictionary<string, ReleaseProviderSettings>(StringComparer.Ordinal)
        {
            ["github"] = new ReleaseProviderSettings { Mirrors = ["https://ghproxy.net/"] }
        };
        settings.Configs["demo"] = new SvchostConfigSettings(
            "services:\n  api:\n    source: { path: /tmp/api }",
            new LockModel
            {
                Services = new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal)
                {
                    ["api"] = new LockServiceEntry
                    {
                        Source = new LockSource
                        {
                            Kind = "path",
                            Path = "/tmp/api",
                            Sha256 = new string('b', 64),
                            Size = 3,
                            FetchedAt = DateTimeOffset.UtcNow
                        },
                        ServiceId = Guid.CreateVersion7(),
                        RouteIds = [Guid.CreateVersion7()]
                    },
                    ["release-api"] = new LockServiceEntry
                    {
                        Source = new LockSource
                        {
                            Kind = "release",
                            ProviderKey = "github",
                            Spec = "owner/repo@v1.2.3",
                            Tag = "v1.2.3",
                            Version = "1.2.3",
                            AssetName = "api_v1.2.3_x64.zip",
                            Sha256 = null,
                            Size = 4,
                            FetchedAt = DateTimeOffset.UtcNow
                        },
                        ServiceId = Guid.CreateVersion7(),
                        RouteIds = []
                    }
                }
            },
            stopped: ["api"]);
        var configurationApi = new FakeConfigurationApi();
        var store = new SettingsStore(configurationApi);

        var write = await store.WriteSettingsAsync(0, settings);
        var read = await store.ReadSettingsAsync();

        Assert.True(write.IsSuccess);
        Assert.True(read.IsSuccess);
        Assert.Equal(1, configurationApi.WriteSettingsCallCount);
        Assert.Contains("\"apiKey\":null", configurationApi.WrittenSettings.Single().SettingsJson, StringComparison.Ordinal);
        Assert.Contains("\"releaseProviders\"", configurationApi.WrittenSettings.Single().SettingsJson, StringComparison.Ordinal);
        Assert.Contains("\"mirrors\":[\"https://ghproxy.net/\"]", configurationApi.WrittenSettings.Single().SettingsJson, StringComparison.Ordinal);
        Assert.Contains("\"observability\":{\"logLevel\":\"Warning\"}", configurationApi.WrittenSettings.Single().SettingsJson, StringComparison.Ordinal);
        Assert.Equal("https://ghproxy.net/", read.Value!.Settings!.ReleaseProviders!["github"].Mirrors!.Single());
        Assert.Null(read.Value!.Settings!.ApiKey);
        Assert.Equal("Warning", read.Value.Settings.Observability.LogLevel);
        Assert.Equal(settings.Routes.Api, read.Value.Settings.Routes.Api);
        Assert.Equal(settings.Configs["demo"].Yaml, read.Value.Settings.Configs["demo"].Yaml);
        Assert.Equal(["api"], read.Value.Settings.Configs["demo"].Stopped);
        Assert.Equal(new string('b', 64), read.Value.Settings.Configs["demo"].Lock.Services["api"].Source.Sha256);
        var settingsJson = configurationApi.WrittenSettings.Single().SettingsJson;
        Assert.Contains("\"providerKey\":\"github\"", settingsJson, StringComparison.Ordinal);
        Assert.Contains("\"spec\":\"owner/repo@v1.2.3\"", settingsJson, StringComparison.Ordinal);
        var releaseLock = read.Value.Settings.Configs["demo"].Lock.Services["release-api"].Source;
        Assert.Equal("v1.2.3", releaseLock.Tag);
        Assert.Equal("1.2.3", releaseLock.Version);
        Assert.Equal("api_v1.2.3_x64.zip", releaseLock.AssetName);
        Assert.Null(releaseLock.Sha256);
    }

    [Theory]
    [InlineData("not-a-level")]
    [InlineData(null)]
    public async Task SettingsStore_roundtrips_unrecognized_observability_log_levels(string? logLevel)
    {
        var settings = CreateSettings();
        settings.Observability.LogLevel = logLevel!;
        var configurationApi = new FakeConfigurationApi();
        var store = new SettingsStore(configurationApi);

        var write = await store.WriteSettingsAsync(0, settings);
        var read = await store.ReadSettingsAsync();

        Assert.True(write.IsSuccess);
        Assert.True(read.IsSuccess);
        Assert.Equal(logLevel, read.Value!.Settings!.Observability.LogLevel);
    }

    [Fact]
    public async Task SettingsStore_roundtrips_explicit_null_observability_as_default()
    {
        var settings = CreateSettings();
        var rawJson = "{\"apiKey\":null,\"routes\":{\"api\":\"" + settings.Routes.Api +
                      "\",\"webui\":\"" + settings.Routes.Webui +
                      "\"},\"observability\":null,\"configs\":{}}";
        var configurationApi = new FakeConfigurationApi();
        var store = new SettingsStore(configurationApi);

        var write = await store.WriteRawSettingsAsync(0, rawJson);
        var read = await store.ReadSettingsAsync();

        Assert.True(write.IsSuccess);
        Assert.True(read.IsSuccess);
        Assert.Equal("information", read.Value!.Settings!.Observability.LogLevel);
        Assert.Equal(rawJson, read.Value.RawJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsStore_accepts_missing_or_null_release_providers(bool includeNullProperty)
    {
        var settings = CreateSettings();
        var optionalProviders = includeNullProperty ? ",\"releaseProviders\":null" : string.Empty;
        var rawJson = "{\"apiKey\":null,\"routes\":{\"api\":\"" + settings.Routes.Api +
                      "\",\"webui\":\"" + settings.Routes.Webui + "\"},\"configs\":{}" + optionalProviders + "}";
        var persisted = new ExtensionSettingsConfiguration(
            SvchostSettingsSchema.ExtensionId,
            schemaVersion: 1,
            rawJson,
            version: 1);
        var store = new SettingsStore(new FakeConfigurationApi(persisted));

        var result = await store.ReadSettingsAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(includeNullProperty, result.Value!.Settings!.ReleaseProviders is null);
        Assert.Equal("information", result.Value!.Settings!.Observability.LogLevel);
    }

    private static SvchostSettings CreateSettings() =>
        new(
            null,
            new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()));
}
