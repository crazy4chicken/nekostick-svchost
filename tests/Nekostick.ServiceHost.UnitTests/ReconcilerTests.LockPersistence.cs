using Xunit;

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

            var report = await reconciler.ReconcileAsync();

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
    public async Task Reconcile_changed_local_source_replaces_once_and_writes_settings_once()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(fixture.SourcePath, "api-v2");
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync();

            Assert.True(report.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(1, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }
}
