using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Sync;

namespace Nekostick.ServiceHost.Settings;

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

            if (settings is null)
            {
                ConfigurationReadResult<HostConfigurationSnapshot> fullConfigurationRead;
                try
                {
                    fullConfigurationRead = await _bridge.FullConfiguration.ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    ReportDegraded("settings-unavailable");
                    return FailureResult(new[]
                    {
                        new ConfigurationError(
                            ConfigurationErrorCode.StorageUnavailable,
                            "The Host configuration snapshot could not be read while checking missing settings.")
                    });
                }

                if (!fullConfigurationRead.IsSuccess || fullConfigurationRead.Value is null)
                {
                    ReportDegraded("settings-unavailable");
                    return FailureResult(fullConfigurationRead.Errors);
                }

                ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>> ownedServicesRead;
                try
                {
                    ownedServicesRead = await _bridge.Services.ReadOwnedAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    ReportDegraded("settings-unavailable");
                    return FailureResult(new[]
                    {
                        new ConfigurationError(
                            ConfigurationErrorCode.StorageUnavailable,
                            "The owned service snapshot could not be read while checking missing settings.")
                    });
                }

                if (!ownedServicesRead.IsSuccess || ownedServicesRead.Value.IsDefault)
                {
                    ReportDegraded("settings-unavailable");
                    return ownedServicesRead.IsSuccess
                        ? FailureResult(new[]
                        {
                            new ConfigurationError(
                                ConfigurationErrorCode.StorageUnavailable,
                                "The owned service snapshot was unavailable while checking missing settings.")
                        })
                        : FailureResult(ownedServicesRead.Errors);
                }

                var hostConfiguration = fullConfigurationRead.Value!;
                var ownedServices = ownedServicesRead.Value!;
                var hasOwnedRoutes = hostConfiguration.Routes.Any(route =>
                    string.Equals(route.OwnerExtensionId, SvchostSettingsSchema.ExtensionId, StringComparison.Ordinal) ||
                    Reconciler.IsOwnedRoute(route.MetadataJson));
                var hasLegacySettings = hostConfiguration.ExtensionSettings.Any(settings =>
                    string.Equals(settings.ExtensionId, SvchostSettingsSchema.LegacyExtensionId, StringComparison.Ordinal));
                if (hasOwnedRoutes || hasLegacySettings || !ownedServices.IsDefaultOrEmpty)
                {
                    return new ApiKeyInitializationResult(
                        true,
                        false,
                        false,
                        null,
                        ConfigurationErrorCode.NoSettings);
                }

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

                var candidate = SvchostSettings.CreateInitial();
                var write = await _settingsStore.WriteSettingsAsync(
                        snapshot.Version,
                        candidate,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (write.IsSuccess)
                {
                    // Read the persisted winner rather than assuming this candidate is still current.
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

                if (HasError(write.Errors, ConfigurationErrorCode.Unsupported) && _bridge.HostInfo.ReadOnly)
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
