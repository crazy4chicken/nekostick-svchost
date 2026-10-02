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

            var settings = read.Settings!;

            if (!settings.Configs.TryGetValue(name, out var config) || config is null)
            {
                return Error(404, "not_found", "The configuration was not found.");
            }

            var compose = TryParseCompose(config, _composeFileParser);
            var serviceScope = compose?.ServiceScope ?? ComposeServiceScope.Global;
            var serviceNames = ServiceNames(config, compose);
            var remainingGlobalServiceNames = new HashSet<string>(StringComparer.Ordinal);
            if (serviceScope == ComposeServiceScope.Global)
            {
                foreach (var pair in settings.Configs)
                {
                    if (string.Equals(pair.Key, name, StringComparison.Ordinal) || pair.Value is null)
                    {
                        continue;
                    }

                    var siblingCompose = TryParseCompose(pair.Value, _composeFileParser);
                    if (siblingCompose?.ServiceScope != ComposeServiceScope.Document)
                    {
                        remainingGlobalServiceNames.UnionWith(ServiceNames(pair.Value, siblingCompose));
                    }
                }
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
            TryDeleteConfigDirectory(name, serviceScope, serviceNames, remainingGlobalServiceNames);
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

        var report = await ReconcileAndRememberAsync(
                Array.Empty<Guid>(),
                Array.Empty<Guid>(),
                cancellationToken)
            .ConfigureAwait(false);
        return SyncResponse(report, name);
    }

    private void TryDeleteConfigDirectory(
        string configName,
        ComposeServiceScope serviceScope,
        IReadOnlyCollection<string> serviceNames,
        IReadOnlySet<string> remainingGlobalServiceNames)
    {
        var dataDirectory = _bridge.DataDirectory;
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return;
        }

        try
        {
            var svchostRoot = Path.GetFullPath(Path.Combine(dataDirectory, "svchost"));
            if (serviceScope == ComposeServiceScope.Document)
            {
                if (string.Equals(configName, "global", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var documentRoot = ServiceRootPath.Resolve(dataDirectory, serviceScope, configName);
                if (IsPathWithin(svchostRoot, documentRoot) && Directory.Exists(documentRoot))
                {
                    TryDeletePath(documentRoot);
                }

                return;
            }

            var globalRoot = ServiceRootPath.Resolve(dataDirectory, ComposeServiceScope.Global, configName);
            var artifactDirectory = Path.GetFullPath(Path.Combine(globalRoot, "artifacts"));
            if (!IsPathWithin(svchostRoot, globalRoot) ||
                !IsPathWithin(globalRoot, artifactDirectory) ||
                !Directory.Exists(artifactDirectory))
            {
                return;
            }

            // Keep artifacts still declared by another global config in the shared namespace.
            foreach (var serviceName in serviceNames)
            {
                if (!IsValidName(serviceName) || remainingGlobalServiceNames.Contains(serviceName))
                {
                    continue;
                }

                var artifactPath = Path.GetFullPath(Path.Combine(artifactDirectory, serviceName));
                if (IsPathWithin(artifactDirectory, artifactPath))
                {
                    TryDeletePath(artifactPath);
                }
            }
        }
        catch (IOException)
        {
            // Cleanup is best-effort after the settings entry has been removed.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best-effort after the settings entry has been removed.
        }
    }

    private static bool IsPathWithin(string parentDirectory, string candidate) =>
        candidate.StartsWith(parentDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void TryDeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Cleanup is best-effort after the settings entry has been removed.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best-effort after the settings entry has been removed.
        }
    }
}
