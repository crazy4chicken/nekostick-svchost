using Xunit;

namespace Nekolla.Nekostick.ServiceHost.UnitTests;

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

            var report = await reconciler.ReconcileAsync();

            Assert.True(report.Succeeded);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_two_consecutive_no_change_runs_skip_replace_and_settings_write()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);

            var firstReport = await reconciler.ReconcileAsync();
            var secondReport = await reconciler.ReconcileAsync();

            Assert.True(firstReport.Succeeded);
            Assert.True(secondReport.Succeeded);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }
}
