using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.ServiceHost.Settings;

public sealed partial class ApiKeyService
{
    /// <summary>Reads settings and selects permanent, bootstrap, or read-only authentication.</summary>
    public async ValueTask<ApiKeyInitializationResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ResetState();

        for (var attempt = 0; attempt < MaxProbeAttempts; attempt++)
        {
            // NOTE: extension StartAsync runs inside the host publish pipeline, before the
            // host reports Ready; gating on HostInfo readiness here would deadlock first
            // startup. Transient storage failures are covered by the retry paths below.
            var hostInfo = _bridge.HostInfo;

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
            if (hostInfo.ReadOnly)
            {
                SetReadonly(settings);
                return new ApiKeyInitializationResult(
                    true,
                    true,
                    false,
                    settings,
                    ConfigurationErrorCode.Unsupported);
            }

            if (settings is null)
            {
                var candidate = SvchostSettings.CreateInitial();
                var write = await _settingsStore.WriteSettingsAsync(
                        snapshot.Version,
                        candidate,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (write.IsSuccess)
                {
                    // The write may have retried after a conflict and adopted another writer's
                    // raw JSON. Always activate the settings currently persisted by the store.
                    var adopted = await _settingsStore.ReadSettingsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!adopted.IsSuccess)
                    {
                        if (HasError(adopted.Errors, ConfigurationErrorCode.StorageUnavailable) &&
                            attempt + 1 < MaxProbeAttempts)
                        {
                            await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        ReportDegraded("settings-unavailable");
                        return FailureResult(adopted.Errors);
                    }

                    settings = adopted.Value!.Settings;
                    if (settings is null)
                    {
                        if (attempt + 1 < MaxProbeAttempts)
                        {
                            await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        ReportDegraded("settings-unavailable");
                        return new ApiKeyInitializationResult(
                            false,
                            false,
                            false,
                            null,
                            ConfigurationErrorCode.StorageUnavailable);
                    }

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
                    SetReadonly(candidate);
                    return new ApiKeyInitializationResult(
                        true,
                        true,
                        false,
                        candidate,
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

            Activate(settings);
            return new ApiKeyInitializationResult(
                true,
                false,
                IsBootstrap,
                settings,
                null);
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
