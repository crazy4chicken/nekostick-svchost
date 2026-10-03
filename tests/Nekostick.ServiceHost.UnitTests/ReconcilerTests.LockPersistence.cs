using Xunit;
using Nekostick.ServiceHost.Sync;

namespace Nekostick.ServiceHost.UnitTests;

public sealed partial class ReconcilerTests
{
    [Fact]
    public async Task Reconcile_stopped_service_is_replaced_as_disabled()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "api.bin");
            await File.WriteAllTextAsync(sourcePath, "api-v1");
            var dataDirectory = Path.Combine(root, "data");
            var artifactPath = Path.Combine(dataDirectory, "svchost", "demo", "artifacts", "api");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.Copy(sourcePath, artifactPath);
            var serviceId = Guid.CreateVersion7();
            var settings = CreatePathSettings(sourcePath, serviceId, stopped: ["api"]);
            var full = new FakeFullConfigurationApi(CreateSnapshot());
            var reconciler = CreateReconciler(new FakeConfigurationApi(ToExtensionSettings(settings)), full, dataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
            var changedService = Assert.Single(full.LastChanges!.Services);
            Assert.Equal(serviceId, changedService.Id);
            Assert.False(changedService.Enabled);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Reconcile_changed_local_source_refreshes_lock_without_replacing_equal_configuration()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(fixture.SourcePath, "api-v2");
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            var serviceReport = Assert.Single(report.Services);
            Assert.True(serviceReport.Changed);
            Assert.Equal(ServiceDecision.Reused, serviceReport.Decision);
            Assert.Empty(serviceReport.Diffs);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Equal("api-v2", await File.ReadAllTextAsync(
                Path.Combine(fixture.DataDirectory, "svchost", "demo", "artifacts", "api")));
            Assert.Equal(1, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_partial_source_failure_persists_source_lock_for_reuse()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(fixture.SourcePath, "api-v2");
            var missingSourcePath = Path.Combine(fixture.Root, "missing.bin");
            fixture.Settings.Configs["demo"]!.Yaml = $"""
                serviceScope: document
                services:
                  api:
                    source:
                      path: {fixture.SourcePath}
                  broken:
                    source:
                      path: {missingSourcePath}
                """;
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);

            var firstReport = await reconciler.ReconcileAsync("test");

            Assert.False(firstReport.Succeeded);
            Assert.True(firstReport.Services.Single(service => service.ServiceName == "api").Succeeded);
            Assert.True(firstReport.Services.Single(service => service.ServiceName == "api").Changed);
            Assert.False(firstReport.Services.Single(service => service.ServiceName == "broken").Succeeded);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Equal(1, configurationApi.WriteSettingsCallCount);

            var secondReport = await reconciler.ReconcileAsync("test");

            Assert.False(secondReport.Succeeded);
            Assert.True(secondReport.Services.Single(service => service.ServiceName == "api").Succeeded);
            Assert.False(secondReport.Services.Single(service => service.ServiceName == "api").Changed);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Equal(1, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }
}
