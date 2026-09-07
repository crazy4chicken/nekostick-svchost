using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.ServiceHost.Compose;
using Nekolla.Nekostick.ServiceHost.Settings;
using Nekolla.Nekostick.ServiceHost.Sync;

namespace Nekolla.Nekostick.ServiceHost.Api;

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
        foreach (var configPair in read.Settings!.Configs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var config = configPair.Value;
            if (config is null)
            {
                continue;
            }

            var names = ServiceNames(config);
            foreach (var serviceName in names)
            {
                LockServiceEntry? lockEntry = null;
                config.Lock?.Services?.TryGetValue(serviceName, out lockEntry);
                var serviceId = lockEntry is null || lockEntry.ServiceId == Guid.Empty
                    ? (Guid?)null
                    : lockEntry.ServiceId;
                byId.TryGetValue(lockEntry?.ServiceId ?? Guid.Empty, out var runtime);
                services.Add(
                    new
                    {
                        config = configPair.Key,
                        service = serviceName,
                        serviceId,
                        enabled = !(config.Stopped ?? Array.Empty<string>())
                            .Contains(serviceName, StringComparer.Ordinal),
                        state = runtime?.LifecycleState.ToString(),
                        detail = runtime?.HealthState.ToString(),
                        routeIds = lockEntry?.RouteIds ?? new List<Guid>(),
                        runtime = RuntimeProjection(runtime)
                    });
            }
        }

        return JsonResponse(200, new { services });
    }

    private async ValueTask<ExtensionStreamingResponse> HandleServiceActionAsync(
        string configName,
        string serviceName,
        string action,
        CancellationToken cancellationToken)
    {
        if (!IsValidName(configName) || !IsValidName(serviceName))
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
                var stopWrite = await SetStoppedAsync(configName, serviceName, true, cancellationToken)
                    .ConfigureAwait(false);
                if (!stopWrite.IsSuccess)
                {
                    return ActionFailureResponse(
                        "restart",
                        configName,
                        serviceName,
                        ConfigurationFailureMessage(stopWrite));
                }

                var stopReport = await ReconcileAndRememberAsync(
                        Array.Empty<Guid>(),
                        Array.Empty<Guid>(),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!stopReport.Succeeded)
                {
                    return ActionResponse(
                        stopReport,
                        "restart",
                        configName,
                        serviceName,
                        "Restart requested; supervisor lifecycle changes are asynchronous and the disable phase failed.");
                }

                var startWrite = await SetStoppedAsync(configName, serviceName, false, cancellationToken)
                    .ConfigureAwait(false);
                if (!startWrite.IsSuccess)
                {
                    return ActionFailureResponse(
                        "restart",
                        configName,
                        serviceName,
                        ConfigurationFailureMessage(startWrite));
                }

                var startReport = await ReconcileAndRememberAsync(
                        Array.Empty<Guid>(),
                        Array.Empty<Guid>(),
                        cancellationToken)
                    .ConfigureAwait(false);
                return ActionResponse(
                    startReport,
                    "restart",
                    configName,
                    serviceName,
                    "Restart requested as asynchronous supervisor disable-then-enable reconciliation; process state is not synchronous.");
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
                    Array.Empty<Guid>(),
                    Array.Empty<Guid>(),
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
