using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.ServiceHost.Settings;
using Nekolla.Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekolla.Nekostick.ServiceHost.UnitTests;

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

    [Fact]
    public async Task SettingsStore_roundtrips_settings_with_null_api_key()
    {
        var settings = CreateSettings();
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
        Assert.Null(read.Value!.Settings!.ApiKey);
        Assert.Equal(settings.Routes.Api, read.Value.Settings.Routes.Api);
        Assert.Equal(settings.Configs["demo"].Yaml, read.Value.Settings.Configs["demo"].Yaml);
        Assert.Equal(["api"], read.Value.Settings.Configs["demo"].Stopped);
        Assert.Equal(new string('b', 64), read.Value.Settings.Configs["demo"].Lock.Services["api"].Source.Sha256);
    }

    private static SvchostSettings CreateSettings() =>
        new(
            null,
            new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()));
}
