using System.Collections.Immutable;
using System.Reflection;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Api;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
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
        var fullConfiguration = new FakeFullConfigurationApi(CreateHostConfigurationSnapshot());
        var bridge = new FakeBridge
        {
            ConfigurationApi = configurationApi,
            FullConfiguration = fullConfiguration
        };
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
        Assert.Equal(1, configurationApi.WriteSettingsCallCount);
        Assert.Equal(1, fullConfiguration.ReadCallCount);
        Assert.Equal(1, bridge.ServiceApi.ReadOwnedCallCount);
    }

    [Fact]
    public async Task Initialize_nonreadonly_unsupported_write_fails_without_activating_candidate()
    {
        var configurationApi = new FakeConfigurationApi
        {
            NextWriteResult = ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Unsupported, "Settings writes are temporarily unavailable."))
        };
        var bridge = CreateFreshInstallBridge(configurationApi);
        var service = new ApiKeyService(new SettingsStore(configurationApi), bridge);

        var initialization = await service.InitializeAsync();

        Assert.False(initialization.Succeeded);
        Assert.False(initialization.Readonly);
        Assert.False(initialization.Bootstrap);
        Assert.Equal(ConfigurationErrorCode.Unsupported, initialization.ErrorCode);
        Assert.False(service.IsReadonly);
        Assert.False(service.IsBootstrap);
        Assert.Null(service.Settings);
        Assert.Null(configurationApi.CurrentSettings);
        Assert.Equal(1, configurationApi.WriteSettingsCallCount);
        Assert.DoesNotContain(
            bridge.LogWriterSink.Entries,
            entry => entry.Message.Contains("bootstrap api key:", StringComparison.Ordinal));
        Assert.Contains(
            bridge.StatusSink.Entries,
            status => status.Kind == ExtensionStatusKind.Degraded && status.Code == "settings-write-failed");
    }

    [Fact]
    public async Task Initialize_uses_readonly_mode_when_host_info_reports_readonly()
    {
        var configurationApi = new FakeConfigurationApi();
        var fullConfiguration = new FakeFullConfigurationApi(CreateHostConfigurationSnapshot());
        var bridge = new FakeBridge
        {
            ConfigurationApi = configurationApi,
            FullConfiguration = fullConfiguration,
            HostInfo = CreateReadOnlyHostInfoSnapshot()
        };
        var service = new ApiKeyService(new SettingsStore(configurationApi), bridge);

        var initialization = await service.InitializeAsync();

        Assert.True(initialization.Succeeded);
        Assert.True(initialization.Readonly);
        Assert.False(initialization.Bootstrap);
        Assert.True(service.IsReadonly);
        Assert.Equal(ConfigurationErrorCode.Unsupported, initialization.ErrorCode);
        Assert.Equal(0, configurationApi.WriteSettingsCallCount);
        Assert.Null(configurationApi.CurrentSettings);
        Assert.Equal(1, fullConfiguration.ReadCallCount);
        Assert.Equal(1, bridge.ServiceApi.ReadOwnedCallCount);
    }

    [Fact]
    public async Task Initialize_treats_unsupported_write_as_readonly_only_when_host_info_is_readonly()
    {
        var configurationApi = new FakeConfigurationApi
        {
            NextWriteResult = ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Unsupported, "Settings writes are temporarily unavailable."))
        };
        var bridge = CreateFreshInstallBridge(configurationApi);
        configurationApi.BeforeWriteSettings = () =>
            bridge.HostInfo = CreateReadOnlyHostInfoSnapshot();
        var service = new ApiKeyService(new SettingsStore(configurationApi), bridge);

        var initialization = await service.InitializeAsync();

        Assert.True(initialization.Succeeded);
        Assert.True(initialization.Readonly);
        Assert.False(initialization.Bootstrap);
        Assert.Equal(ConfigurationErrorCode.Unsupported, initialization.ErrorCode);
        Assert.True(service.IsReadonly);
        Assert.Null(service.Settings);
        Assert.Null(configurationApi.CurrentSettings);
        Assert.Equal(1, configurationApi.WriteSettingsCallCount);
        Assert.DoesNotContain(
            bridge.StatusSink.Entries,
            status => status.Code == "settings-write-failed");
    }

    [Fact]
    public async Task SetPermanentKey_nonreadonly_unsupported_write_preserves_current_authentication()
    {
        const string permanentKey = "replacement-permanent-api-key";
        var configurationApi = new FakeConfigurationApi();
        var bridge = CreateFreshInstallBridge(configurationApi);
        var service = new ApiKeyService(new SettingsStore(configurationApi), bridge);

        var initialization = await service.InitializeAsync();
        var bootstrapLog = Assert.Single(bridge.LogWriterSink.Entries);
        var bootstrapKey = bootstrapLog.Message[(bootstrapLog.Message.LastIndexOf(':') + 1)..].Trim();
        Assert.True(initialization.Succeeded);
        Assert.True(await service.VerifyAsync(bootstrapKey));

        configurationApi.NextWriteResult = ConfigurationWriteResult.Failure(
            new ConfigurationError(ConfigurationErrorCode.Unsupported, "Settings writes are temporarily unavailable."));
        var result = await service.SetPermanentKeyAsync(permanentKey);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Unsupported, Assert.Single(result.Errors).Code);
        Assert.False(service.IsReadonly);
        Assert.True(service.IsBootstrap);
        Assert.True(await service.VerifyAsync(bootstrapKey));
        Assert.False(await service.VerifyAsync(permanentKey));
        Assert.Null(service.Settings!.ApiKey);
        Assert.Equal(2, configurationApi.WriteSettingsCallCount);
        Assert.Contains(
            bridge.StatusSink.Entries,
            status => status.Kind == ExtensionStatusKind.Degraded && status.Code == "settings-write-failed");
    }

    [Fact]
    public async Task Entry_revokes_api_key_in_memory_when_own_settings_are_deleted_without_writing()
    {
        const string apiKey = "settings-removal-api-key-123";
        var configurationApi = new FakeConfigurationApi();
        var settingsStore = new SettingsStore(configurationApi);
        configurationApi.UpdateSettings(settingsStore.CreateExtensionSettingsConfiguration(
            new SvchostSettings(apiKey, new SvchostRouteSettings()),
            version: 1));
        var bridge = new FakeBridge { ConfigurationApi = configurationApi, DataDirectory = Environment.CurrentDirectory };
        var fullConfiguration = Assert.IsType<FakeFullConfigurationApi>(bridge.FullConfiguration);
        var entry = new SvchostEntry();
        try
        {
            await entry.StartAsync(new FakeStartContext(bridge, new FakeRegistration()), CancellationToken.None);
            var apiKeyService = Assert.IsType<ApiKeyService>(
                typeof(SvchostEntry)
                    .GetField("_apiKeyService", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(entry));

            Assert.True(await apiKeyService.VerifyAsync(apiKey));

            var reconciliationCompleted = WaitForSyncFailureAsync(bridge);
            configurationApi.RemoveSettings();
            await bridge.EventPublisher.DispatchAsync(new ExtensionEvent(
                nameof(ExtensionCoreEventKind.ExtensionSettingsChanged),
                1,
                "{\"extensionId\":\"nekostick.svchost\"}"));

            var syncStatus = await reconciliationCompleted;
            Assert.Equal(ExtensionStatusKind.Degraded, syncStatus.Kind);
            var report = GetLastRunFailure(entry);
            Assert.Equal(ConfigurationErrorCode.Validation, report.ErrorCode);
            Assert.Equal("settings-event", report.Trigger);
            Assert.False(await apiKeyService.VerifyAsync(apiKey));
            Assert.False(apiKeyService.IsBootstrap);
            Assert.Null(apiKeyService.Settings);
            Assert.Null(configurationApi.CurrentSettings);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
            Assert.Equal(0, fullConfiguration.ReplaceCallCount);
        }
        finally
        {
            await entry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Entry_preserves_api_key_when_settings_event_read_is_storage_unavailable()
    {
        const string apiKey = "settings-read-unavailable-api-key";
        var configurationApi = new FakeConfigurationApi();
        var settingsStore = new SettingsStore(configurationApi);
        configurationApi.UpdateSettings(settingsStore.CreateExtensionSettingsConfiguration(
            new SvchostSettings(apiKey, new SvchostRouteSettings()),
            version: 1));
        var bridge = new FakeBridge { ConfigurationApi = configurationApi, DataDirectory = Environment.CurrentDirectory };
        var fullConfiguration = Assert.IsType<FakeFullConfigurationApi>(bridge.FullConfiguration);
        var entry = new SvchostEntry();
        try
        {
            await entry.StartAsync(new FakeStartContext(bridge, new FakeRegistration()), CancellationToken.None);
            var apiKeyService = Assert.IsType<ApiKeyService>(
                typeof(SvchostEntry)
                    .GetField("_apiKeyService", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(entry));
            Assert.True(await apiKeyService.VerifyAsync(apiKey));

            var reconciliationCompleted = WaitForSyncFailureAsync(bridge);
            configurationApi.SettingsReadOverride = ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(ConfigurationErrorCode.StorageUnavailable, "Settings are temporarily unavailable."));
            await bridge.EventPublisher.DispatchAsync(new ExtensionEvent(
                nameof(ExtensionCoreEventKind.ExtensionSettingsChanged),
                1,
                "{\"extensionId\":\"nekostick.svchost\"}"));

            var syncStatus = await reconciliationCompleted;
            Assert.Equal(ExtensionStatusKind.Degraded, syncStatus.Kind);
            var report = GetLastRunFailure(entry);
            Assert.Equal(ConfigurationErrorCode.StorageUnavailable, report.ErrorCode);
            Assert.Equal("settings-event", report.Trigger);
            Assert.True(await apiKeyService.VerifyAsync(apiKey));
            Assert.Equal(apiKey, apiKeyService.Settings!.ApiKey);
            Assert.NotNull(configurationApi.CurrentSettings);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
            Assert.Equal(0, fullConfiguration.ReplaceCallCount);
        }
        finally
        {
            await entry.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Entry_requires_settings_restore_when_owned_routes_exist(
        bool handlerOwnedRoute,
        bool hostReadOnly)
    {
        var serviceId = Guid.CreateVersion7();
        var route = handlerOwnedRoute
            ? CreateOwnedHandlerHistoryRoute()
            : CreateOwnedManagedRoute(serviceId);
        var services = handlerOwnedRoute
            ? ImmutableArray<ServiceConfiguration>.Empty
            : ImmutableArray.Create(CreateHostService(serviceId));
        var configurationApi = new FakeConfigurationApi();
        var fullConfiguration = new FakeFullConfigurationApi(CreateHostConfigurationSnapshot(
            ImmutableArray.Create(route),
            services));
        var bridge = new FakeBridge
        {
            ConfigurationApi = configurationApi,
            FullConfiguration = fullConfiguration,
            HostInfo = hostReadOnly ? CreateReadOnlyHostInfoSnapshot() : ExtensionHostInfoSnapshot.Unavailable
        };
        var registration = new FakeRegistration();
        var entry = new SvchostEntry();
        try
        {
            await entry.StartAsync(new FakeStartContext(bridge, registration), CancellationToken.None);

            var apiKeyService = GetApiKeyService(entry);
            Assert.False(apiKeyService.IsReadonly);
            Assert.False(apiKeyService.IsBootstrap);
            Assert.Null(apiKeyService.Settings);
            Assert.False(await apiKeyService.VerifyAsync("must-not-bootstrap"));
            Assert.Null(configurationApi.CurrentSettings);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
            Assert.Equal(0, fullConfiguration.ReplaceCallCount);
            Assert.Equal(1, bridge.ServiceApi.ReadOwnedCallCount);
            Assert.Equal(0, bridge.ServiceApi.UpsertCallCount);
            Assert.Equal(0, bridge.ServiceApi.RemoveCallCount);
            Assert.Single(fullConfiguration.Snapshot.Routes);
            Assert.All(fullConfiguration.Snapshot.Services, service => Assert.True(service.Enabled));
            Assert.Empty(registration.RegisteredHandlers);
            Assert.Contains(bridge.StatusSink.Entries, status => status.Code == "settings-missing");
        }
        finally
        {
            await entry.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_requires_settings_restore_when_owned_services_exist(bool hostReadOnly)
    {
        var serviceId = Guid.CreateVersion7();
        var configurationApi = new FakeConfigurationApi();
        var fullConfiguration = new FakeFullConfigurationApi(CreateHostConfigurationSnapshot(
            services: ImmutableArray.Create(CreateHostService(serviceId))));
        var bridge = new FakeBridge
        {
            ConfigurationApi = configurationApi,
            FullConfiguration = fullConfiguration,
            HostInfo = hostReadOnly ? CreateReadOnlyHostInfoSnapshot() : ExtensionHostInfoSnapshot.Unavailable
        };
        bridge.ServiceApi.OwnedServices = ImmutableArray.Create(CreateOwnedService(serviceId));
        var registration = new FakeRegistration();
        var entry = new SvchostEntry();
        try
        {
            await entry.StartAsync(new FakeStartContext(bridge, registration), CancellationToken.None);

            var apiKeyService = GetApiKeyService(entry);
            Assert.False(apiKeyService.IsReadonly);
            Assert.False(apiKeyService.IsBootstrap);
            Assert.Null(apiKeyService.Settings);
            Assert.False(await apiKeyService.VerifyAsync("must-not-bootstrap"));
            Assert.Null(configurationApi.CurrentSettings);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
            Assert.Equal(0, fullConfiguration.ReplaceCallCount);
            Assert.Equal(0, bridge.ServiceApi.UpsertCallCount);
            Assert.Equal(0, bridge.ServiceApi.RemoveCallCount);
            Assert.Equal(1, bridge.ServiceApi.ReadOwnedCallCount);
            Assert.True(Assert.Single(fullConfiguration.Snapshot.Services).Enabled);
            Assert.Empty(registration.RegisteredHandlers);
            Assert.Contains(bridge.StatusSink.Entries, status => status.Code == "settings-missing");
        }
        finally
        {
            await entry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Entry_preserves_legacy_settings_history_when_migration_write_fails()
    {
        var configurationApi = new FakeConfigurationApi();
        var currentSettings = new SettingsStore(configurationApi).CreateExtensionSettingsConfiguration(
            SvchostSettings.CreateInitial(),
            version: 1);
        var legacySettings = new ExtensionSettingsConfiguration(
            SvchostSettingsSchema.LegacyExtensionId,
            currentSettings.SchemaVersion,
            currentSettings.SettingsJson,
            currentSettings.Version);
        var fullConfiguration = new FakeFullConfigurationApi(CreateHostConfigurationSnapshot(
            extensionSettings: ImmutableArray.Create(legacySettings)))
        {
            NextReplaceResult = ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation, "Legacy settings migration was rejected."))
        };
        var bridge = new FakeBridge
        {
            ConfigurationApi = configurationApi,
            FullConfiguration = fullConfiguration,
            DataDirectory = Environment.CurrentDirectory
        };
        var registration = new FakeRegistration();
        var entry = new SvchostEntry();
        try
        {
            await entry.StartAsync(new FakeStartContext(bridge, registration), CancellationToken.None);

            var apiKeyService = GetApiKeyService(entry);
            Assert.False(apiKeyService.IsReadonly);
            Assert.False(apiKeyService.IsBootstrap);
            Assert.Null(apiKeyService.Settings);
            Assert.False(await apiKeyService.VerifyAsync("must-not-bootstrap"));
            Assert.Null(configurationApi.CurrentSettings);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
            Assert.DoesNotContain(
                bridge.LogWriterSink.Entries,
                log => log.Message.Contains("bootstrap api key:", StringComparison.Ordinal));
            Assert.Contains(bridge.LoggerSink.Entries, log => log.Code == "legacy-settings-migration-failed");
            Assert.Equal(1, fullConfiguration.ReplaceCallCount);
            var attemptedChanges = Assert.Single(fullConfiguration.ChangesHistory);
            Assert.Empty(attemptedChanges.Routes);
            Assert.Empty(attemptedChanges.Services);
            Assert.Empty(fullConfiguration.Snapshot.Routes);
            Assert.Empty(fullConfiguration.Snapshot.Services);
            Assert.Equal(legacySettings, Assert.Single(fullConfiguration.Snapshot.ExtensionSettings));
            Assert.Equal(1, bridge.ServiceApi.ReadOwnedCallCount);
            Assert.Equal(0, bridge.ServiceApi.UpsertCallCount);
            Assert.Equal(0, bridge.ServiceApi.RemoveCallCount);
            Assert.Empty(registration.RegisteredHandlers);
            Assert.Contains(bridge.StatusSink.Entries, status => status.Code == "settings-missing");
        }
        finally
        {
            await entry.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Initialize_fails_closed_when_ownership_snapshot_read_is_unavailable(
        bool hostReadOnly,
        bool ownedServicesReadUnavailable)
    {
        var configurationApi = new FakeConfigurationApi();
        var fullConfiguration = new FakeFullConfigurationApi(CreateHostConfigurationSnapshot())
        {
            NextReadResult = ownedServicesReadUnavailable
                ? null
                : ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.StorageUnavailable, "The Host snapshot is unavailable."))
        };
        var bridge = new FakeBridge
        {
            ConfigurationApi = configurationApi,
            FullConfiguration = fullConfiguration,
            HostInfo = hostReadOnly ? CreateReadOnlyHostInfoSnapshot() : ExtensionHostInfoSnapshot.Unavailable
        };
        bridge.ServiceApi.ReadResultOverride = ownedServicesReadUnavailable
            ? ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>.Failure(
                new ConfigurationError(ConfigurationErrorCode.StorageUnavailable, "The owned service snapshot is unavailable."))
            : null;
        var service = new ApiKeyService(new SettingsStore(configurationApi), bridge);

        var initialization = await service.InitializeAsync();

        Assert.False(initialization.Succeeded);
        Assert.False(initialization.Readonly);
        Assert.False(initialization.Bootstrap);
        Assert.Equal(ConfigurationErrorCode.StorageUnavailable, initialization.ErrorCode);
        Assert.False(service.IsReadonly);
        Assert.False(service.IsBootstrap);
        Assert.Null(service.Settings);
        Assert.Null(configurationApi.CurrentSettings);
        Assert.Equal(0, configurationApi.WriteSettingsCallCount);
        Assert.Equal(1, fullConfiguration.ReadCallCount);
        Assert.Equal(ownedServicesReadUnavailable ? 1 : 0, bridge.ServiceApi.ReadOwnedCallCount);
        Assert.Contains(
            bridge.StatusSink.Entries,
            status => status.Kind == ExtensionStatusKind.Degraded && status.Code == "settings-unavailable");
    }

    [Fact]
    public async Task Initialize_retries_conflicting_initial_settings_write_after_rereading()
    {
        var configurationApi = new FakeConfigurationApi
        {
            NextWriteResult = ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict, "The settings changed."))
        };
        var service = new ApiKeyService(new SettingsStore(configurationApi), CreateFreshInstallBridge(configurationApi));

        var initialization = await service.InitializeAsync();

        Assert.True(initialization.Succeeded);
        Assert.True(initialization.Bootstrap);
        Assert.Equal(2, configurationApi.WriteSettingsCallCount);
        Assert.NotNull(configurationApi.CurrentSettings);
    }

    [Fact]
    public async Task SetPermanentKey_persists_key_and_exits_bootstrap()
    {
        var configurationApi = new FakeConfigurationApi();
        var bridge = CreateFreshInstallBridge(configurationApi);
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
        var bridge = CreateFreshInstallBridge(configurationApi);
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
        var bridge = CreateFreshInstallBridge();
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
        var bridge = CreateFreshInstallBridge();
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
        var bridge = CreateFreshInstallBridge();
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

    private static Task<ExtensionStatus> WaitForSyncFailureAsync(FakeBridge bridge)
    {
        var completed = new TaskCompletionSource<ExtensionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.StatusSink.Reported = status =>
        {
            if (status.Code == "sync-failed")
            {
                completed.TrySetResult(status);
            }
        };
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static SyncReport GetLastRunFailure(SvchostEntry entry)
    {
        var handler = Assert.IsType<SvchostApiHandler>(
            typeof(SvchostEntry)
                .GetField("_apiHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(entry));
        return Assert.IsType<SyncReport>(
            typeof(SvchostApiHandler)
                .GetField("_lastRunFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(handler));
    }

    private static ApiKeyService GetApiKeyService(SvchostEntry entry) =>
        Assert.IsType<ApiKeyService>(
            typeof(SvchostEntry)
                .GetField("_apiKeyService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(entry));

    private static HostConfigurationSnapshot CreateHostConfigurationSnapshot(
        ImmutableArray<RouteConfiguration> routes = default,
        ImmutableArray<ServiceConfiguration> services = default,
        ImmutableArray<ExtensionSettingsConfiguration> extensionSettings = default) =>
        new(
            0,
            new GlobalSettingsConfiguration(),
            routes.IsDefault ? ImmutableArray<RouteConfiguration>.Empty : routes,
            services.IsDefault ? ImmutableArray<ServiceConfiguration>.Empty : services,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            extensionSettings.IsDefault ? ImmutableArray<ExtensionSettingsConfiguration>.Empty : extensionSettings);

    private static RouteConfiguration CreateOwnedManagedRoute(Guid serviceId)
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new RouteConfiguration(
            Guid.CreateVersion7(),
            true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Prefix,
                "/history",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            new MicroserviceRouteTargetConfiguration(serviceId),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            """{"owner":"nekostick.svchost","config":"history","service":"api"}""",
            createdAt,
            createdAt,
            1);
    }

    private static RouteConfiguration CreateOwnedHandlerHistoryRoute()
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new RouteConfiguration(
            Guid.CreateVersion7(),
            true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Prefix,
                "/svchost",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            new ExtensionHandlerRouteTargetConfiguration(SvchostApiHandler.StableHandlerId),
            100,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            "{}",
            createdAt,
            createdAt,
            1,
            ownerExtensionId: SvchostSettingsSchema.ExtensionId);
    }

    private static ServiceConfiguration CreateHostService(Guid serviceId)
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new ServiceConfiguration(
            serviceId,
            true,
            "owned-service",
            ImmutableArray<string>.Empty,
            ".",
            ImmutableDictionary<string, string>.Empty,
            ServiceStartMode.Eager,
            ServiceRestartPolicy.OnFailure,
            new ServiceHealthCheckConfiguration(ServiceHealthCheckType.Process, null, TimeSpan.FromSeconds(5)),
            createdAt,
            createdAt,
            1);
    }

    private static ExtensionServiceConfiguration CreateOwnedService(Guid serviceId)
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new ExtensionServiceConfiguration(
            serviceId,
            true,
            "owned-service",
            ImmutableArray<string>.Empty,
            ".",
            ServiceStartMode.Eager,
            ServiceRestartPolicy.OnFailure,
            new ServiceHealthCheckConfiguration(ServiceHealthCheckType.Process, null, TimeSpan.FromSeconds(5)),
            createdAt,
            createdAt,
            1);
    }

    private static FakeBridge CreateFreshInstallBridge(FakeConfigurationApi? configurationApi = null) =>
        new()
        {
            ConfigurationApi = configurationApi ?? new FakeConfigurationApi(),
            FullConfiguration = new FakeFullConfigurationApi(CreateHostConfigurationSnapshot())
        };

    private static ExtensionHostInfoSnapshot CreateReadOnlyHostInfoSnapshot() =>
        new(
            null,
            readOnly: true,
            extensionsSkipped: false,
            supervisorDisabled: false,
            databaseAvailable: false,
            snapshotAvailable: false,
            configurationValid: false,
            publishedConfigurationVersion: null,
            lastSnapshotState: ExtensionHostSnapshotState.Unknown,
            lastSnapshotStateAt: null,
            readiness: ExtensionHostReadinessState.Unknown);
}
