using System.Reflection;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Api;
using Nekostick.ServiceHost.Settings;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class ApiKeyServiceTests
{
    [Fact]
    public async Task Entry_rejects_bridge_thirteen_only_even_when_api_is_fourteen()
    {
        var bridge = DispatchProxy.Create<IExtensionHostBridge13, Bridge13OnlyProxy>();
        var proxy = (Bridge13OnlyProxy)(object)bridge;
        var registration = new FakeRegistration();
        var entry = new SvchostEntry();

        await entry.StartAsync(new FakeStartContext(bridge, registration), CancellationToken.None);

        Assert.Empty(registration.RegisteredHandlers);
        Assert.Contains(proxy.LoggerSink.Entries, item => item.Code == "api-14-bridge-unavailable");
    }

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
        Assert.Equal(
            "The API key must contain at least 16 characters and cannot be blank.",
            Assert.Single(result.Errors).Message);
        Assert.Equal(writesBefore, configurationApi.WriteSettingsCallCount);
        Assert.True(service.IsBootstrap);
    }

    [Fact]
    public async Task Entry_rejects_api_below_fourteen_before_registering_handlers()
    {
        var bridge = new FakeBridge { ApiVersion = new HostApiVersion(1, 3, 3) };
        var registration = new FakeRegistration();
        var entry = new SvchostEntry();

        await entry.StartAsync(new FakeStartContext(bridge, registration), CancellationToken.None);

        Assert.Empty(registration.RegisteredHandlers);
        Assert.Empty(bridge.TaskScheduler.TaskNames);
        Assert.Contains(bridge.LoggerSink.Entries, item => item.Code == "api-14-unsupported");
    }

    [Theory]
    [InlineData(ExtensionRegistrationFailureCode.Conflict)]
    [InlineData(ExtensionRegistrationFailureCode.Unsupported)]
    public async Task Entry_preserves_registration_failure_detail_and_rolls_back_other_handler(
        ExtensionRegistrationFailureCode code)
    {
        var bridge = new FakeBridge();
        var registration = new FakeRegistration();
        const string reason = "The API handler registration was rejected: exact host detail.";
        registration.RegistrationResults[SvchostApiHandler.StableHandlerId] =
            ExtensionRegistrationResult.Failure(code, new ExtensionErrorDetail(reason));
        var entry = new SvchostEntry();
        try
        {
            await entry.StartAsync(new FakeStartContext(bridge, registration), CancellationToken.None);

            Assert.Equal(new[] { "nekostick.svchost.webui" }, registration.UnregisteredHandlers);
            Assert.Empty(bridge.TaskScheduler.TaskNames);
            Assert.Contains(bridge.LogWriterSink.Entries, item => item.Message.Contains($"{code}: {reason}", StringComparison.Ordinal));
            Assert.Contains(bridge.StatusSink.Entries, status => status.Code == "handler-registration-failed");
        }
        finally
        {
            await entry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Entry_keeps_startup_degraded_and_retains_event_task_and_cleanup_failure_details()
    {
        var bridge = new FakeBridge();
        var registration = new FakeRegistration();
        const string eventReason = "The event queue has stopped accepting subscriptions.";
        const string taskReason = "The task scheduler has reached its capacity.";
        const string cleanupReason = "The handler was already tombstoned.";
        bridge.EventPublisher.SubscribeResult = ExtensionEventSubscribeResult.Failure(
            ExtensionEventSubscribeFailureCode.Unavailable, new ExtensionErrorDetail(eventReason));
        bridge.TaskScheduler.Result = ExtensionTaskStartResult.Failure(
            ExtensionTaskStartFailureCode.LimitReached, new ExtensionErrorDetail(taskReason));
        registration.UnregistrationResult = ExtensionRegistrationResult.Failure(
            ExtensionRegistrationFailureCode.NotFound, new ExtensionErrorDetail(cleanupReason));
        var entry = new SvchostEntry();
        try
        {
            await entry.StartAsync(new FakeStartContext(bridge, registration), CancellationToken.None);

            Assert.Equal(new[] { "sync" }, bridge.TaskScheduler.TaskNames);
            Assert.Contains(bridge.StatusSink.Entries, status => status.Code == "settings-event-subscription-failed");
            Assert.Contains(bridge.StatusSink.Entries, status => status.Code == "initial-sync-not-scheduled");
            Assert.DoesNotContain(bridge.StatusSink.Entries, status => status.Kind == ExtensionStatusKind.Healthy);
            Assert.Contains(bridge.LogWriterSink.Entries, item => item.Message.Contains($"Unavailable: {eventReason}", StringComparison.Ordinal));
            Assert.Contains(bridge.LogWriterSink.Entries, item => item.Message.Contains($"LimitReached: {taskReason}", StringComparison.Ordinal));
        }
        finally
        {
            await entry.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, registration.UnregisteredHandlers.Count);
        Assert.Contains(bridge.LogWriterSink.Entries, item => item.Message.Contains($"NotFound: {cleanupReason}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Entry_retains_task_exception_reason_without_aborting_startup()
    {
        var bridge = new FakeBridge();
        const string reason = "The task scheduler could not dispatch the callback.";
        bridge.TaskScheduler.Exception = new InvalidOperationException(reason);
        var entry = new SvchostEntry();
        try
        {
            await entry.StartAsync(new FakeStartContext(bridge, new FakeRegistration()), CancellationToken.None);

            Assert.Contains(bridge.StatusSink.Entries, status => status.Code == "initial-sync-not-scheduled");
            Assert.Contains(bridge.LogWriterSink.Entries, item => item.Message.Contains(reason, StringComparison.Ordinal));
        }
        finally
        {
            await entry.StopAsync(CancellationToken.None);
        }
    }
}
