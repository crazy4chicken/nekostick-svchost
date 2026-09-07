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
            .Select(pair => new
            {
                name = pair.Key,
                services = ServiceNames(pair.Value),
                @lock = LockSummary(pair.Value),
                lastSync = GetLastReport(pair.Key) is { } report
                    ? SyncReportPayload(report, pair.Key)
                    : null
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
        if (!IsValidName(name))
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
                    lastSync = GetLastReport(name) is { } report
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

            var report = await ReconcileAndRememberAsync(
                    Array.Empty<Guid>(),
                    Array.Empty<Guid>(),
                    cancellationToken)
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

            // Capture all identities before removing the settings entry so Reconciler can
            // remove the corresponding global assets even though the lock is gone afterward.
            var serviceIds = (config.Lock?.Services?.Values ?? Enumerable.Empty<LockServiceEntry>())
                .Where(entry => entry is not null && entry.ServiceId != Guid.Empty)
                .Select(entry => entry!.ServiceId)
                .ToArray();
            var routeIds = (config.Lock?.Services?.Values ?? Enumerable.Empty<LockServiceEntry>())
                .Where(entry => entry is not null)
                .SelectMany(entry => entry!.RouteIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .ToArray();

            var write = await _settingsStore.UpdateSettingsAsync(
                    settings =>
                    {
                        settings.Configs.Remove(name);
                        return settings;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ErrorForConfiguration(write.Errors);
            }

            var report = await ReconcileAndRememberAsync(serviceIds, routeIds, cancellationToken)
                .ConfigureAwait(false);
            TryDeleteConfigDirectory(name);
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
        if (!IsValidName(name))
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

        var report = await ReconcileAndRememberAsync(
                Array.Empty<Guid>(),
                Array.Empty<Guid>(),
                cancellationToken)
            .ConfigureAwait(false);
        return SyncResponse(report, name);
    }

    private void TryDeleteConfigDirectory(string configName)
    {
        if (string.IsNullOrWhiteSpace(_bridge.DataDirectory))
        {
            return;
        }

        try
        {
            var root = Path.GetFullPath(Path.Combine(_bridge.DataDirectory, "svchost"));
            var directory = Path.GetFullPath(Path.Combine(root, configName));
            if (directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // The Host configuration is already authoritative; a later sync can recreate the directory.
        }
        catch (UnauthorizedAccessException)
        {
            // The Host configuration is already authoritative; a later sync can recreate the directory.
        }
    }
}
