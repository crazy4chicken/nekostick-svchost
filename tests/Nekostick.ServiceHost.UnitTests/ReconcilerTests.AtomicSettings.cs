using System.Security.Cryptography;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed partial class ReconcilerTests
{
    [Fact]
    public async Task Reconcile_atomically_commits_changed_artifact_and_lock()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(fixture.SourcePath, "api-v2");
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
            var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SourcePath)))
                .ToLowerInvariant();
            var service = Assert.Single(full.Snapshot.Services);
            Assert.Equal(
                Path.Combine(fixture.DataDirectory, "svchost", "demo", "artifacts", "sha256", "api", digest, "api"),
                service.FileName);

            var settingsRow = Assert.Single(
                full.Snapshot.ExtensionSettings,
                entry => entry.ExtensionId == SvchostSettingsSchema.ExtensionId);
            var persistedSettings = JsonSerializer.Deserialize<SvchostSettings>(settingsRow.SettingsJson)!;
            Assert.Equal(digest, persistedSettings.Configs["demo"].Lock.Services["api"].Source!.Sha256);
            Assert.Equal(settingsRow.Version, report.CommittedSettingsVersion);
            Assert.Equal(full.Snapshot.Version, report.WrittenConfigurationVersion);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_observes_Host_settings_version_bump_after_unchanged_StageA_settings()
    {
        var fixture = await CreateReusablePathFixtureAsync(changedSnapshot: true);
        try
        {
            var serializedSettings = ToExtensionSettings(fixture.Settings);
            const long settingsVersion = 19;
            var canonicalSettings = new ExtensionSettingsConfiguration(
                serializedSettings.ExtensionId,
                serializedSettings.SchemaVersion,
                CanonicalizeHostJson(serializedSettings.SettingsJson),
                settingsVersion);
            var snapshot = new HostConfigurationSnapshot(
                fixture.Snapshot.Version,
                fixture.Snapshot.GlobalSettings,
                fixture.Snapshot.Routes,
                fixture.Snapshot.Services,
                fixture.Snapshot.ExtensionRecords,
                [canonicalSettings]);
            var full = new FakeFullConfigurationApi(snapshot)
            {
                IncrementSettingsVersionOnEveryReplace = true
            };
            var configurationApi = new FakeConfigurationApi(canonicalSettings);
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");
            var nextReport = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.True(nextReport.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(settingsVersion, report.ConsumedSettingsVersion);
            Assert.Equal(settingsVersion + 1, report.CommittedSettingsVersion);
            Assert.Equal(report.CommittedSettingsVersion, nextReport.ConsumedSettingsVersion);
            Assert.Equal(settingsVersion + 1, configurationApi.CurrentSettings!.Version);
            Assert.Equal(
                settingsVersion + 1,
                Assert.Single(full.Snapshot.ExtensionSettings,
                    entry => entry.ExtensionId == SvchostSettingsSchema.ExtensionId).Version);
            Assert.Equal(
                fixture.Snapshot.Version + 1,
                report.WrittenConfigurationVersion);
            Assert.Same(
                canonicalSettings,
                Assert.Single(full.ChangesHistory[0].ExtensionSettings,
                    entry => entry.ExtensionId == SvchostSettingsSchema.ExtensionId));
            Assert.Equal(canonicalSettings.SettingsJson,
                full.Snapshot.ExtensionSettings.Single(
                    entry => entry.ExtensionId == SvchostSettingsSchema.ExtensionId).SettingsJson);
            Assert.Null(nextReport.WrittenConfigurationVersion);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_does_not_overwrite_settings_or_Host_state_changed_during_replace()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            var existingService = Assert.Single(fixture.Snapshot.Services);
            var previousArtifact = await File.ReadAllTextAsync(existingService.FileName);
            await File.WriteAllTextAsync(fixture.SourcePath, "api-v2");
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var settingsStore = new SettingsStore(configurationApi);
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);
            var currentRow = configurationApi.CurrentSettings!;
            var concurrentSettings = JsonSerializer.Deserialize<SvchostSettings>(currentRow.SettingsJson)!;
            concurrentSettings.Configs["demo"].Stopped = ["api"];
            var competingRow = new ExtensionSettingsConfiguration(
                currentRow.ExtensionId,
                currentRow.SchemaVersion,
                settingsStore.SerializeSettings(concurrentSettings),
                currentRow.Version + 1);
            full.BeforeReplace = host =>
            {
                configurationApi.UpdateSettings(competingRow);
                host.RecordSettingsWrite(competingRow);
            };

            var report = await reconciler.ReconcileAsync("test");

            Assert.False(report.Succeeded);
            Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, report.ErrorCode);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(fixture.Snapshot.Version, full.LastExpectedVersion);
            Assert.Equal(currentRow.Version, full.LastChanges!.ExtensionSettings.Single(
                entry => entry.ExtensionId == SvchostSettingsSchema.ExtensionId).Version);
            Assert.Null(report.WrittenConfigurationVersion);
            Assert.Null(report.CommittedSettingsVersion);
            Assert.Equal(existingService.FileName, Assert.Single(full.Snapshot.Services).FileName);
            Assert.Equal(existingService.Version, Assert.Single(full.Snapshot.Services).Version);
            Assert.Equal(previousArtifact, await File.ReadAllTextAsync(existingService.FileName));

            var persistedRow = Assert.Single(
                full.Snapshot.ExtensionSettings,
                entry => entry.ExtensionId == SvchostSettingsSchema.ExtensionId);
            Assert.Equal(competingRow.Version, persistedRow.Version);
            var persistedSettings = JsonSerializer.Deserialize<SvchostSettings>(persistedRow.SettingsJson)!;
            Assert.Contains("api", persistedSettings.Configs["demo"].Stopped);
            Assert.Equal(
                fixture.Settings.Configs["demo"].Lock.Services["api"].Source!.Sha256,
                persistedSettings.Configs["demo"].Lock.Services["api"].Source!.Sha256);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }
}
