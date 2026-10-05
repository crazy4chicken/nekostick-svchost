using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Settings;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class LegacySettingsMigrationTests
{
    [Fact]
    public async Task MigrateAsync_moves_legacy_settings_and_preserves_other_settings()
    {
        var legacy = new ExtensionSettingsConfiguration(
            SvchostSettingsSchema.LegacyExtensionId,
            7,
            "{\"apiKey\":\"legacy\"}",
            42);
        var other = new ExtensionSettingsConfiguration(
            "other.extension",
            3,
            "{\"enabled\":true}",
            9);
        var fullConfiguration = new FakeFullConfigurationApi(CreateSnapshot(legacy, other));

        var result = await new LegacySettingsMigration(fullConfiguration).MigrateAsync();

        Assert.Equal(LegacySettingsMigrationResult.Migrated, result);
        Assert.Equal(1, fullConfiguration.ReplaceCallCount);
        var migrated = Assert.Single(
            fullConfiguration.Snapshot.ExtensionSettings,
            settings => settings.ExtensionId == SvchostSettingsSchema.ExtensionId);
        Assert.Equal(legacy.SettingsJson, migrated.SettingsJson);
        Assert.Equal(legacy.SchemaVersion, migrated.SchemaVersion);
        Assert.Equal(0, migrated.Version);
        Assert.DoesNotContain(
            fullConfiguration.Snapshot.ExtensionSettings,
            settings => settings.ExtensionId == SvchostSettingsSchema.LegacyExtensionId);
        Assert.Same(
            other,
            Assert.Single(
                fullConfiguration.Snapshot.ExtensionSettings,
                settings => settings.ExtensionId == other.ExtensionId));
    }

    [Fact]
    public async Task MigrateAsync_preserves_current_settings_when_both_identifiers_exist()
    {
        var legacy = new ExtensionSettingsConfiguration(
            SvchostSettingsSchema.LegacyExtensionId,
            1,
            "{\"legacy\":true}",
            2);
        var current = new ExtensionSettingsConfiguration(
            SvchostSettingsSchema.ExtensionId,
            SvchostSettingsSchema.CurrentVersion,
            "{\"current\":true}",
            5);
        var snapshot = CreateSnapshot(legacy, current);
        var fullConfiguration = new FakeFullConfigurationApi(snapshot);

        var result = await new LegacySettingsMigration(fullConfiguration).MigrateAsync();

        Assert.Equal(LegacySettingsMigrationResult.SkippedExisting, result);
        Assert.Equal(0, fullConfiguration.ReplaceCallCount);
        Assert.Same(snapshot, fullConfiguration.Snapshot);
    }

    [Fact]
    public async Task MigrateAsync_returns_none_when_legacy_settings_are_absent()
    {
        var fullConfiguration = new FakeFullConfigurationApi(CreateSnapshot(
            new ExtensionSettingsConfiguration("other.extension", 1, "{}", 4)));

        var result = await new LegacySettingsMigration(fullConfiguration).MigrateAsync();

        Assert.Equal(LegacySettingsMigrationResult.None, result);
        Assert.Equal(0, fullConfiguration.ReplaceCallCount);
    }

    [Fact]
    public async Task MigrateAsync_returns_failed_when_read_fails()
    {
        var fullConfiguration = new FakeFullConfigurationApi(CreateSnapshot());
        fullConfiguration.NextReadResult = ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
            new ConfigurationError(ConfigurationErrorCode.StorageUnavailable, "The configuration store could not be read."));

        var result = await new LegacySettingsMigration(fullConfiguration).MigrateAsync();

        Assert.Equal(LegacySettingsMigrationResult.Failed, result);
        Assert.Equal(0, fullConfiguration.ReplaceCallCount);
    }

    private static HostConfigurationSnapshot CreateSnapshot(
        params ExtensionSettingsConfiguration[] extensionSettings) =>
        new(
            12,
            new GlobalSettingsConfiguration(),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            extensionSettings.ToImmutableArray());
}
