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
    public async Task Reconcile_two_consecutive_no_change_runs_skip_replace_resume_and_settings_write()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var supervisorApi = CountingSupervisorProxy.Create(out var supervisor);
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory, supervisorApi);

            var firstReport = await reconciler.ReconcileAsync("test");
            var replaceCallCountAfterFirst = full.ReplaceCallCount;
            var resumeCallCountAfterFirst = supervisor.ResumeCallCount;
            var secondReport = await reconciler.ReconcileAsync("test");

            Assert.True(firstReport.Succeeded);
            Assert.True(secondReport.Succeeded);
            Assert.Null(firstReport.WrittenConfigurationVersion);
            Assert.Null(secondReport.WrittenConfigurationVersion);
            Assert.Equal(replaceCallCountAfterFirst, full.ReplaceCallCount);
            Assert.Equal(resumeCallCountAfterFirst, supervisor.ResumeCallCount);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Equal(0, supervisor.ReadCallCount);
            Assert.Equal(0, supervisor.ResumeCallCount);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }
}
