using Nekolla.Nekostick.ServiceHost.Settings;
using Xunit;

namespace Nekolla.Nekostick.ServiceHost.UnitTests;

public sealed class ApiKeyServiceTests
{
    [Fact]
    public async Task Initialize_bootstraps_key_and_verification_accepts_only_logged_key()
    {
        var configurationApi = new FakeConfigurationApi();
        var bridge = new FakeBridge();
        var service = new ApiKeyService(new SettingsStore(configurationApi), bridge);

        var initialization = await service.InitializeAsync();
        var bootstrapLog = Assert.Single(bridge.LogWriterSink.Entries);
        var bootstrapKey = bootstrapLog.Message[(bootstrapLog.Message.LastIndexOf(':') + 1)..].Trim();

        Assert.True(initialization.Succeeded);
        Assert.True(initialization.Bootstrap);
        Assert.True(service.IsBootstrap);
        Assert.Null(initialization.Settings!.ApiKey);
        Assert.True(await service.VerifyAsync(bootstrapKey));
        Assert.False(await service.VerifyAsync(bootstrapKey + "wrong"));
        Assert.False(await service.VerifyAsync(null));
    }

    [Fact]
    public async Task SetPermanentKey_persists_key_and_exits_bootstrap()
    {
        var configurationApi = new FakeConfigurationApi();
        var bridge = new FakeBridge();
        var service = new ApiKeyService(new SettingsStore(configurationApi), bridge);
        await service.InitializeAsync();

        const string permanentKey = "a-permanent-key-123";
        var result = await service.SetPermanentKeyAsync(permanentKey);

        Assert.True(result.IsSuccess);
        Assert.False(service.IsBootstrap);
        Assert.Equal(permanentKey, service.Settings!.ApiKey);
        Assert.True(await service.VerifyAsync(permanentKey));
        Assert.False(await service.VerifyAsync("not-the-permanent-key"));
        Assert.True(configurationApi.WriteSettingsCallCount >= 2);
        Assert.Equal(permanentKey, configurationApi.WrittenSettings.Last().SettingsJson.Contains(permanentKey, StringComparison.Ordinal)
            ? permanentKey
            : null);
    }

    [Fact]
    public async Task SetPermanentKey_rejects_keys_shorter_than_sixteen_characters_without_writing()
    {
        var configurationApi = new FakeConfigurationApi();
        var bridge = new FakeBridge();
        var service = new ApiKeyService(new SettingsStore(configurationApi), bridge);
        await service.InitializeAsync();
        var writesBefore = configurationApi.WriteSettingsCallCount;

        var result = await service.SetPermanentKeyAsync("too-short");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Code == Nekolla.Nekostick.Contracts.ConfigurationErrorCode.Validation);
        Assert.Equal(writesBefore, configurationApi.WriteSettingsCallCount);
        Assert.True(service.IsBootstrap);
    }
}
