using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;

namespace Nekostick.ServiceHost.Api;

public sealed partial class SvchostApiHandler
{
    private async ValueTask<ExtensionStreamingResponse> HandleListConfigsAsync(CancellationToken cancellationToken)
    {
        var read = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (read.Error is not null)
        {
            return read.Error;
        }

        var settings = read.Settings!;
        var configs = settings.Configs
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var compose = TryParseCompose(pair.Value, _composeFileParser);
                return new
                {
                    name = pair.Key,
                    services = ServiceNames(pair.Value, compose),
                    serviceScope = compose is null
                        ? null
                        : compose.ServiceScope switch
                        {
                            ComposeServiceScope.Global => "global",
                            ComposeServiceScope.Document => "document",
                            _ => null
                        },
                    strictSources = compose?.StrictSources,
                    @lock = LockSummary(pair.Value),
                    lastSync = GetEffectiveReport(pair.Key) is { } report
                        ? SyncReportPayload(report, pair.Key)
                        : null
                };
            })
            .ToArray();

        return JsonResponse(200, configs);
    }

    private async ValueTask<ExtensionStreamingResponse> HandleConfigAsync(
        string method,
        string name,
        Stream bodyStream,
        CancellationToken cancellationToken)
    {
        if (!IsValidConfigName(name))
        {
            return Error(404, "not_found", "The configuration was not found.");
        }

        var normalizedMethod = method.ToUpperInvariant();
        if (normalizedMethod == "GET")
        {
            var read = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (read.Error is not null)
            {
                return read.Error;
            }

            if (!read.Settings!.Configs.TryGetValue(name, out var config) || config is null)
            {
                return Error(404, "not_found", "The configuration was not found.");
            }

            return JsonResponse(
                200,
                new
                {
                    name,
                    yaml = config.Yaml,
                    @lock = config.Lock,
                    lastSync = GetEffectiveReport(name) is { } report
                        ? SyncReportPayload(report, name)
                        : null
                });
        }

        if (normalizedMethod == "PUT")
        {
            return await HandlePutConfigAsync(name, bodyStream, cancellationToken).ConfigureAwait(false);
        }

        if (normalizedMethod == "DELETE")
        {
            return await HandleDeleteConfigAsync(name, cancellationToken).ConfigureAwait(false);
        }

        return Error(404, "not_found", "The API endpoint was not found.");
    }

    private async ValueTask<ExtensionStreamingResponse> HandlePutConfigAsync(
        string name,
        Stream bodyStream,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(bodyStream, cancellationToken).ConfigureAwait(false);
        if (body.TooLarge)
        {
            return Error(413, "request_too_large", "The request body exceeds the 1 MiB limit.");
        }

        ConfigRequest? input;
        try
        {
            input = JsonSerializer.Deserialize<ConfigRequest>(body.Bytes, JsonOptions);
        }
        catch (JsonException)
        {
            return Error(422, "validation", "The request body must be valid JSON.");
        }

        if (input is null || input.Yaml is null)
        {
            return Error(422, "validation", "The request body must contain a yaml string.");
        }

        try
        {
            _composeFileParser.Parse(input.Yaml);
        }
        catch (ComposeValidationException exception)
        {
            return Error(422, "validation", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Error(422, "validation", exception.Message);
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var write = await _settingsStore.UpdateSettingsAsync(
                    settings =>
                    {
                        if (settings.Configs.TryGetValue(name, out var existing) && existing is not null)
                        {
                            existing.Yaml = input.Yaml;
                            existing.Lock ??= new LockModel();
                            existing.Stopped ??= Array.Empty<string>();
                        }
                        else
                        {
                            settings.Configs[name] = new SvchostConfigSettings(input.Yaml);
                        }

                        return settings;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ErrorForConfiguration(write.Errors);
            }

            var report = await ReconcileAndRememberAsync("api-config-put", cancellationToken)
                .ConfigureAwait(false);
            return SyncResponse(report, name);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async ValueTask<ExtensionStreamingResponse> HandleDeleteConfigAsync(
        string name,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (read.Error is not null)
            {
                return read.Error;
            }

            if (!read.Settings!.Configs.TryGetValue(name, out var config) || config is null)
            {
                return Error(404, "not_found", "The configuration was not found.");
            }

            var write = await _settingsStore.UpdateSettingsAsync(
                    latestSettings =>
                    {
                        if (latestSettings.Configs.TryGetValue(name, out var latestConfig) &&
                            latestConfig is not null)
                        {
                            var compose = TryParseCompose(latestConfig, _composeFileParser);
                            var scope = compose?.ServiceScope == ComposeServiceScope.Document
                                ? "document"
                                : "global";
                            foreach (var lockPair in latestConfig.Lock?.Services ??
                                     new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal))
                            {
                                var entry = lockPair.Value;
                                if (entry is null || !SvchostSettings.IsUuidV7(entry.ServiceId))
                                {
                                    continue;
                                }

                                SvchostSettings.AddRetirement(
                                    latestSettings.Retiring,
                                    new RetiringServiceSettings(
                                        entry.ServiceId,
                                        (entry.RouteIds ?? new List<Guid>()).Where(SvchostSettings.IsUuidV7),
                                        name,
                                        IsValidName(lockPair.Key) ? lockPair.Key : "orphan",
                                        scope));
                            }

                            latestSettings.Configs.Remove(name);
                        }

                        return latestSettings;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ErrorForConfiguration(write.Errors);
            }

            var report = await ReconcileAndRememberAsync("api-config-delete", cancellationToken)
                .ConfigureAwait(false);
            return JsonResponse(
                200,
                new
                {
                    deleted = true,
                    name,
                    report = SyncReportPayload(report, name)
                });
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async ValueTask<ExtensionStreamingResponse> HandleSyncAsync(
        string name,
        CancellationToken cancellationToken)
    {
        if (!IsValidConfigName(name))
        {
            return Error(404, "not_found", "The configuration was not found.");
        }


        var read = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (read.Error is not null)
        {
            return read.Error;
        }

        if (!read.Settings!.Configs.ContainsKey(name))
        {
            return Error(404, "not_found", "The configuration was not found.");
        }

        var report = await ReconcileAndRememberAsync("api-config-sync", cancellationToken)
            .ConfigureAwait(false);
        return SyncResponse(report, name);
    }

}
