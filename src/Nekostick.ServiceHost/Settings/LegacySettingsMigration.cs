using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.Settings;

/// <summary>Describes the outcome of attempting to migrate legacy extension settings.</summary>
public enum LegacySettingsMigrationResult
{
    /// <summary>No legacy settings were present.</summary>
    None,

    /// <summary>Legacy settings were copied to the current extension identifier.</summary>
    Migrated,

    /// <summary>Current extension settings already existed, so they were preserved.</summary>
    SkippedExisting,

    /// <summary>The migration could not be completed.</summary>
    Failed
}

/// <summary>Migrates settings from the extension identifier used before the rename.</summary>
public sealed class LegacySettingsMigration
{
    private readonly IExtensionFullConfigurationApi _fullConfiguration;

    /// <summary>Creates a migration over the Host full configuration API.</summary>
    public LegacySettingsMigration(IExtensionFullConfigurationApi fullConfiguration)
    {
        _fullConfiguration = fullConfiguration ?? throw new ArgumentNullException(nameof(fullConfiguration));
    }

    /// <summary>Attempts the migration with bounded retries for optimistic concurrency conflicts.</summary>
    public async ValueTask<LegacySettingsMigrationResult> MigrateAsync(
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < SettingsStore.MaxConflictAttempts; attempt++)
        {
            ConfigurationReadResult<HostConfigurationSnapshot> read;
            try
            {
                read = await _fullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return LegacySettingsMigrationResult.Failed;
            }

            if (!read.IsSuccess || read.Value is null)
            {
                return LegacySettingsMigrationResult.Failed;
            }

            var snapshot = read.Value;
            var legacy = snapshot.ExtensionSettings.FirstOrDefault(settings =>
                string.Equals(settings.ExtensionId, SvchostSettingsSchema.LegacyExtensionId, StringComparison.Ordinal));
            if (legacy is null)
            {
                return LegacySettingsMigrationResult.None;
            }

            var currentExists = snapshot.ExtensionSettings.Any(settings =>
                string.Equals(settings.ExtensionId, SvchostSettingsSchema.ExtensionId, StringComparison.Ordinal));
            if (currentExists)
            {
                return LegacySettingsMigrationResult.SkippedExisting;
            }

            var extensionSettings = snapshot.ExtensionSettings
                .Where(settings =>
                    !string.Equals(settings.ExtensionId, SvchostSettingsSchema.LegacyExtensionId, StringComparison.Ordinal))
                .Append(new ExtensionSettingsConfiguration(
                    SvchostSettingsSchema.ExtensionId,
                    legacy.SchemaVersion,
                    legacy.SettingsJson,
                    0))
                .ToImmutableArray();
            var changes = new ConfigurationChangeSet(
                snapshot.GlobalSettings,
                snapshot.Routes,
                snapshot.Services,
                snapshot.ExtensionRecords,
                extensionSettings);

            ConfigurationWriteResult write;
            try
            {
                write = await _fullConfiguration.ReplaceAsync(
                        snapshot.Version,
                        changes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return LegacySettingsMigrationResult.Failed;
            }

            if (write.IsSuccess)
            {
                return LegacySettingsMigrationResult.Migrated;
            }

            if (!write.Errors.Any(error => error.Code == ConfigurationErrorCode.ConcurrencyConflict) ||
                attempt + 1 >= SettingsStore.MaxConflictAttempts)
            {
                return LegacySettingsMigrationResult.Failed;
            }
        }

        return LegacySettingsMigrationResult.Failed;
    }
}
