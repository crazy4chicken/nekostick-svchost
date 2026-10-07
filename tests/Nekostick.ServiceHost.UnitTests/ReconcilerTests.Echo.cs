using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Settings;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed partial class ReconcilerTests
{
    [Fact]
    public async Task Reconcile_identical_desired_state_skips_replace_and_settings_write()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Null(report.WrittenConfigurationVersion);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_resumes_waiting_service_when_artifact_exists_without_Host_replace()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            var serviceId = fixture.Settings.Configs["demo"]!.Lock.Services["api"].ServiceId;
            var waitingRuntime = new ExtensionServiceRuntimeSnapshot(
                serviceId,
                null,
                null,
                null,
                ExtensionServiceLifecycleState.Waiting,
                ExtensionServiceHealthState.Unknown,
                0,
                0,
                null,
                null,
                null);
            var supervisorApi = FixedRuntimeSupervisorProxy.Create(waitingRuntime);
            var supervisor = (FixedRuntimeSupervisorProxy)(object)supervisorApi;
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory, supervisorApi);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Null(report.WrittenConfigurationVersion);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Equal(1, supervisor.ReadCallCount);
            Assert.Equal(1, supervisor.ResumeCallCount);
            Assert.Equal(serviceId, supervisor.ResumedServiceId);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_two_consecutive_no_change_runs_skip_replace_and_settings_write_for_running_service()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var serviceId = fixture.Settings.Configs["demo"]!.Lock.Services["api"].ServiceId;
            var runningRuntime = new ExtensionServiceRuntimeSnapshot(
                serviceId,
                null,
                null,
                null,
                ExtensionServiceLifecycleState.Running,
                ExtensionServiceHealthState.Unknown,
                0,
                0,
                null,
                null,
                null);
            var supervisorApi = FixedRuntimeSupervisorProxy.Create(runningRuntime);
            var supervisor = (FixedRuntimeSupervisorProxy)(object)supervisorApi;
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory, supervisorApi);

            var firstReport = await reconciler.ReconcileAsync("test");
            var secondReport = await reconciler.ReconcileAsync("test");

            Assert.True(firstReport.Succeeded);
            Assert.True(secondReport.Succeeded);
            Assert.Null(firstReport.WrittenConfigurationVersion);
            Assert.Null(secondReport.WrittenConfigurationVersion);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Equal(0, supervisor.ResumeCallCount);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_rehydrating_same_content_preserves_persisted_lock_and_skips_writes()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            var artifactPath = Assert.Single(fixture.Snapshot.Services).FileName;
            File.Delete(artifactPath);

            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var settingsStore = new SettingsStore(configurationApi);
            var initialSettings = await settingsStore.ReadSettingsAsync();
            Assert.True(initialSettings.IsSuccess);
            var initialSource = initialSettings.Value!.Settings!.Configs["demo"]!.Lock.Services["api"].Source;
            var fetchedAt = initialSource.FetchedAt;
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);

            var firstReport = await reconciler.ReconcileAsync("test");
            var firstSettings = await settingsStore.ReadSettingsAsync();
            var secondReport = await reconciler.ReconcileAsync("test");
            var secondSettings = await settingsStore.ReadSettingsAsync();

            Assert.True(firstSettings.IsSuccess);
            Assert.True(secondSettings.IsSuccess);
            var firstSource = firstSettings.Value!.Settings!.Configs["demo"]!.Lock.Services["api"].Source;
            var secondSource = secondSettings.Value!.Settings!.Configs["demo"]!.Lock.Services["api"].Source;
            Assert.True(firstReport.Succeeded);
            Assert.True(secondReport.Succeeded);
            Assert.True(File.Exists(artifactPath));
            Assert.Equal(fixture.Snapshot.Version, full.Snapshot.Version);
            Assert.Equal(initialSettings.Value!.Version, firstSettings.Value!.Version);
            Assert.Equal(initialSettings.Value!.Version, secondSettings.Value!.Version);
            Assert.Equal(initialSource.Kind, firstSource.Kind);
            Assert.Equal(initialSource.Path, firstSource.Path);
            Assert.Equal(initialSource.Sha256, firstSource.Sha256);
            Assert.Equal(initialSource.Size, firstSource.Size);
            Assert.Equal(fetchedAt, firstSource.FetchedAt);
            Assert.Equal(firstSource.Kind, secondSource.Kind);
            Assert.Equal(firstSource.Path, secondSource.Path);
            Assert.Equal(firstSource.Sha256, secondSource.Sha256);
            Assert.Equal(firstSource.Size, secondSource.Size);
            Assert.Equal(fetchedAt, secondSource.FetchedAt);
            Assert.Null(firstReport.WrittenConfigurationVersion);
            Assert.Null(secondReport.WrittenConfigurationVersion);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }
}
