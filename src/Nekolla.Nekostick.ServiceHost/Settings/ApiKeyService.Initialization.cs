using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.ServiceHost.Settings;

public sealed partial class ApiKeyService
{
    /// <summary>Runs the readonly probe and selects permanent or bootstrap authentication.</summary>
    public async ValueTask<ApiKeyInitializationResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ResetState();

        for (var attempt = 0; attempt < MaxProbeAttempts; attempt++)
        {
            var read = await _settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                if (HasError(read.Errors, ConfigurationErrorCode.StorageUnavailable) &&
                    attempt + 1 < MaxProbeAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return FailureResult(read.Errors);
            }

            var snapshot = read.Value!;
            var settings = snapshot.Settings;
            ConfigurationWriteResult write;
            if (settings is null)
            {
                settings = SvchostSettings.CreateInitial();
                write = await _settingsStore.WriteSettingsAsync(
                        snapshot.Version,
                        settings,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // This write intentionally sends the original string rather than a re-serialized model.
                write = await _settingsStore.WriteRawSettingsAsync(
                        snapshot.Version,
                        snapshot.RawJson!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (write.IsSuccess)
            {
                Activate(settings);
                return new ApiKeyInitializationResult(
                    true,
                    false,
                    IsBootstrap,
                    settings,
                    null);
            }

            if (HasError(write.Errors, ConfigurationErrorCode.Unsupported))
            {
                SetReadonly(settings);
                return new ApiKeyInitializationResult(
                    true,
                    true,
                    false,
                    settings,
                    ConfigurationErrorCode.Unsupported);
            }

            if (HasError(write.Errors, ConfigurationErrorCode.StorageUnavailable) &&
                attempt + 1 < MaxProbeAttempts)
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (HasError(write.Errors, ConfigurationErrorCode.ConcurrencyConflict) &&
                attempt + 1 < MaxProbeAttempts)
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            ReportDegraded("settings-write-failed");
            return FailureResult(write.Errors);
        }

        ReportDegraded("settings-unavailable");
        return new ApiKeyInitializationResult(
            false,
            false,
            false,
            null,
            ConfigurationErrorCode.StorageUnavailable);
    }
}
