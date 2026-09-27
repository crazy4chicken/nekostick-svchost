using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed partial class ReconcilerTests
{
    [Fact]
    public async Task Reconcile_changed_desired_state_calls_replace()
    {
        var fixture = await CreateReusablePathFixtureAsync(changedSnapshot: true);
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync();

            Assert.True(report.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
            Assert.Equal(["--changed"], full.LastChanges!.Services.Single().ArgumentList.ToArray());
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }
}
