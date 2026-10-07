using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;

namespace Nekostick.ServiceHost.Api;

public sealed partial class SvchostApiHandler
{
    private async ValueTask<ExtensionStreamingResponse> HandleServicesAsync(CancellationToken cancellationToken)
    {
        var read = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (read.Error is not null)
        {
            return read.Error;
        }

        var telemetry = await _bridge.Supervisor.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!telemetry.IsSuccess)
        {
            return ErrorForConfiguration(telemetry.Errors, fallbackStatus: 502);
        }

        var byId = telemetry.Value.ToDictionary(snapshot => snapshot.ServiceId);
        var services = new List<object>();
        var observedServiceIds = new HashSet<Guid>();
        foreach (var configPair in read.Settings!.Configs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var config = configPair.Value;
            if (config is null)
            {
                continue;
            }

            var configReport = GetEffectiveReport(configPair.Key);
            var names = ServiceNames(config, TryParseCompose(config, _composeFileParser));
            foreach (var serviceName in names)
            {
                LockServiceEntry? lockEntry = null;
                config.Lock?.Services?.TryGetValue(serviceName, out lockEntry);
                var serviceId = lockEntry is null || lockEntry.ServiceId == Guid.Empty
                    ? (Guid?)null
                    : lockEntry.ServiceId;
                byId.TryGetValue(lockEntry?.ServiceId ?? Guid.Empty, out var runtime);
                if (serviceId is { } observedServiceId && observedServiceIds.Add(observedServiceId))
                {
                    if (runtime is null)
                    {
                        ResetServiceObservation(observedServiceId);
                    }
                    else
                    {
                        RecordServiceObservation(observedServiceId, runtime.LifecycleState);
                    }
                }

                var failureSnapshot = runtime is not null && serviceId is { } id
                    ? _serviceFailureTracker.GetSnapshot(id)
                    : default;
                var serviceReport = configReport?.Services.FirstOrDefault(reportService =>
                    string.Equals(reportService.ServiceName, serviceName, StringComparison.Ordinal));
                var lastReconcile = ServiceReconcileProjection(configReport, serviceReport);
                var driftCorrected = configReport is not null &&
                    serviceReport is not null &&
                    ExternalDriftWarning.IsDriftCorrection(configReport.Trigger, serviceReport);
                services.Add(
                    new
                    {
                        config = configPair.Key,
                        service = serviceName,
                        serviceId,
                        enabled = !(config.Stopped ?? Array.Empty<string>())
                            .Contains(serviceName, StringComparer.Ordinal),
                        state = runtime?.LifecycleState.ToString(),
                        detail = DetailFor(runtime),
                        routeIds = lockEntry?.RouteIds ?? new List<Guid>(),
                        source = lockEntry?.Source,
                        runtime = RuntimeProjection(runtime),
                        consecutiveFailures = failureSnapshot.ConsecutiveFailedObservations,
                        lastReconcile,
                        driftCorrected
                    });
            }
        }

        return JsonResponse(200, new { services });
    }

    private static string? DetailFor(ExtensionServiceRuntimeSnapshot? runtime)
    {
        if (runtime is null)
        {
            return null;
        }

        // Failed services carry a bounded human-readable failure reason; prefer
        // it over the health state, which stays stuck at its last observation.
        if (runtime.LifecycleState == ExtensionServiceLifecycleState.Failed &&
            !string.IsNullOrWhiteSpace(runtime.FailureReason))
        {
            return runtime.FailureReason;
        }

        return runtime.HealthState.ToString();
    }

    private async ValueTask<ExtensionStreamingResponse> HandleServiceActionAsync(
        string configName,
        string serviceName,
        string action,
        CancellationToken cancellationToken)
    {
        if (!IsValidConfigName(configName) || !IsValidName(serviceName))
        {
            return Error(404, "not_found", "The service was not found.");
        }

        if (!action.Equals("start", StringComparison.Ordinal) &&
            !action.Equals("stop", StringComparison.Ordinal) &&
            !action.Equals("restart", StringComparison.Ordinal))
        {
            return Error(404, "not_found", "The service operation was not found.");
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settingsRead = await _settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (!settingsRead.IsSuccess)
            {
                return ActionFailureResponse(
                    action,
                    configName,
                    serviceName,
                    ConfigurationReadFailureMessage(settingsRead.Errors));
            }

            var settings = settingsRead.Value?.Settings;
            if (settings is null)
            {
                return ActionFailureResponse(
                    action,
                    configName,
                    serviceName,
                    "Extension settings are not initialized.");
            }

            if (!settings.Configs.TryGetValue(configName, out var config) || config is null)
            {
                return Error(404, "not_found", "The configuration was not found.");
            }

            ComposeFile compose;
            try
            {
                compose = _composeFileParser.Parse(config.Yaml ?? string.Empty);
            }
            catch (ComposeValidationException exception)
            {
                return Error(422, "validation", exception.Message);
            }

            if (!compose.Services.ContainsKey(serviceName))
            {
                return Error(404, "not_found", "The service was not found.");
            }

            if (action.Equals("restart", StringComparison.Ordinal))
            {
                if (config.Lock?.Services is not { } lockedServices ||
                    !lockedServices.TryGetValue(serviceName, out var lockEntry) ||
                    lockEntry is null ||
                    !SvchostSettings.IsUuidV7(lockEntry.ServiceId))
                {
                    return Error(404, "not_found", "The service was not found.");
                }

                var restart = await _bridge.Supervisor.RestartAsync(
                        lockEntry.ServiceId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!restart.IsSuccess)
                {
                    return ActionFailureResponse(
                        "restart",
                        configName,
                        serviceName,
                        ConfigurationFailureMessage(restart));
                }

                var restartReport = new SyncReport(
                    true,
                    !string.IsNullOrWhiteSpace(_bridge.DataDirectory),
                    DateTimeOffset.UtcNow,
                    ImmutableArray.Create(new ServiceSyncReport(
                        configName,
                        serviceName,
                        true,
                        false,
                        lockEntry.ServiceId,
                        (lockEntry.RouteIds ?? new List<Guid>()).ToImmutableArray(),
                        null)
                    {
                        Decision = ServiceDecision.Reused
                    }),
                    null,
                    null)
                {
                    ConsumedSettingsVersion = settingsRead.Value!.Version,
                    Trigger = "api-service-restart"
                };
                RememberReport(restartReport);
                return ActionResponse(
                    restartReport,
                    "restart",
                    configName,
                    serviceName,
                    "Restart requested on this node; process state is asynchronous.");
            }

            var shouldStop = action.Equals("stop", StringComparison.Ordinal);
            var write = await SetStoppedAsync(configName, serviceName, shouldStop, cancellationToken)
                .ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ActionFailureResponse(
                    action,
                    configName,
                    serviceName,
                    ConfigurationFailureMessage(write));
            }

            var report = await ReconcileAndRememberAsync(
                    shouldStop ? "api-service-stop" : "api-service-start",
                    cancellationToken)
                .ConfigureAwait(false);
            var message = shouldStop
                ? "Stop requested; supervisor lifecycle changes are asynchronous and process state is not synchronous."
                : "Start requested; supervisor lifecycle changes are asynchronous and process state is not synchronous.";
            return ActionResponse(report, action, configName, serviceName, message);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async ValueTask<ConfigurationWriteResult> SetStoppedAsync(
        string configName,
        string serviceName,
        bool stopped,
        CancellationToken cancellationToken)
    {
        return await _settingsStore.UpdateSettingsAsync(
                settings =>
                {
                    if (!settings.Configs.TryGetValue(configName, out var config) || config is null)
                    {
                        throw new ArgumentException("The configuration was not found.", nameof(configName));
                    }

                    var names = (config.Stopped ?? Array.Empty<string>())
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .ToHashSet(StringComparer.Ordinal);
                    if (stopped)
                    {
                        names.Add(serviceName);
                    }
                    else
                    {
                        names.Remove(serviceName);
                    }

                    config.Stopped = names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
                    return settings;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
