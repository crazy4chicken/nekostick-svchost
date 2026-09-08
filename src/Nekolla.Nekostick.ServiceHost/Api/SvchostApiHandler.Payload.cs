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
    private static string[] ServiceNames(SvchostConfigSettings? config)
    {
        if (config is null)
        {
            return Array.Empty<string>();
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        if (config.Lock?.Services is not null)
        {
            names.UnionWith(config.Lock.Services.Keys);
        }

        try
        {
            names.UnionWith(new ComposeFileParser().Parse(config.Yaml ?? string.Empty).Services.Keys);
        }
        catch (ComposeValidationException)
        {
            // A malformed document remains visible through its persisted lock summary.
        }

        return names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private static object LockSummary(SvchostConfigSettings? config)
    {
        if (config?.Lock?.Services is null)
        {
            return new { services = new Dictionary<string, object>(StringComparer.Ordinal) };
        }

        return new
        {
            services = config.Lock.Services.ToDictionary(
                pair => pair.Key,
                pair => new
                {
                    serviceId = pair.Value?.ServiceId,
                    routeIds = pair.Value?.RouteIds ?? new List<Guid>(),
                    source = pair.Value?.Source
                },
                StringComparer.Ordinal)
        };
    }

    private static object? RuntimeProjection(ExtensionServiceRuntimeSnapshot? runtime) => runtime is null
        ? null
        : new
        {
            serviceId = runtime.ServiceId,
            processId = runtime.ProcessId,
            startedAt = runtime.StartedAt,
            uptime = runtime.Uptime,
            lifecycleState = runtime.LifecycleState.ToString(),
            healthState = runtime.HealthState.ToString(),
            forwardedRequestCount = runtime.ForwardedRequestCount,
            activeForwardedRequestCount = runtime.ActiveForwardedRequestCount,
            lastUpdatedAt = runtime.LastUpdatedAt,
            lastHealthAt = runtime.LastHealthAt,
            ownerExtensionId = runtime.OwnerExtensionId
        };

    private static ExtensionStreamingResponse SyncResponse(SyncReport report, string configName)
    {
        if (report.ErrorCode == ConfigurationErrorCode.ConcurrencyConflict &&
            report.FailureCode != SyncErrorCode.LockPersistFailed)
        {
            return Error(
                409,
                "conflict",
                report.Error ?? "The synchronization concurrency limit was exhausted.");
        }

        return JsonResponse(200, SyncReportPayload(report, configName));
    }

    private static object SyncReportPayload(SyncReport report, string? configName)
    {
        var services = report.Services
            .Select(service =>
            {
                var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = service.ServiceName,
                    ["succeeded"] = service.Succeeded,
                    ["error"] = service.Error,
                    ["warnings"] = service.Warnings.IsDefaultOrEmpty ? Array.Empty<string>() : service.Warnings.ToArray(),
                    ["nodeLocal"] = service.NodeLocal
                };
                if (service.FailureCode.HasValue)
                {
                    payload["errorKind"] = SyncErrorKind(service.FailureCode);
                }

                return payload;
            })
            .ToArray();

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["succeeded"] = report.Succeeded,
            ["dataDirectoryAvailable"] = report.DataDirectoryAvailable,
            ["completedAt"] = report.CompletedAt,
            ["services"] = services,
            ["notes"] = report.Notes.IsDefaultOrEmpty ? Array.Empty<string>() : report.Notes.ToArray()
        };
        if (configName is not null)
        {
            result["name"] = configName;
        }

        if (report.FailureCode.HasValue)
        {
            result["error"] = report.Error;
            result["errorKind"] = SyncErrorKind(report.FailureCode);
        }

        return result;
    }

    private static ExtensionStreamingResponse ActionResponse(
        SyncReport report,
        string action,
        string configName,
        string serviceName,
        string message)
    {
        var succeeded = report.Succeeded;
        var serviceFailure = report.Services
            .FirstOrDefault(service =>
                string.Equals(service.ConfigName, configName, StringComparison.Ordinal) &&
                string.Equals(service.ServiceName, serviceName, StringComparison.Ordinal) &&
                !service.Succeeded)
            ?.Error;
        var responseMessage = succeeded
            ? message
            : serviceFailure ?? report.Error ?? "The service action reconciliation failed.";
        return JsonResponse(
            200,
            new
            {
                action,
                config = configName,
                service = serviceName,
                succeeded,
                message = responseMessage,
                asynchronous = true
            });
    }

    private static ExtensionStreamingResponse ActionFailureResponse(
        string action,
        string configName,
        string serviceName,
        string message) =>
        JsonResponse(
            200,
            new
            {
                action,
                config = configName,
                service = serviceName,
                succeeded = false,
                message,
                asynchronous = true
            });
    private static string ConfigurationReadFailureMessage(IEnumerable<ConfigurationError> errors) =>
        errors.FirstOrDefault()?.Message ?? "The service action could not read extension settings.";

    private static string ConfigurationFailureMessage(ConfigurationWriteResult result) =>
        result.Errors.FirstOrDefault()?.Message ?? "The service action could not be persisted.";

    private static string? SyncErrorKind(SyncErrorCode? code) => code switch
    {
        SyncErrorCode.SourceFailed => "source",
        SyncErrorCode.ReconcileFailed => "reconcile",
        SyncErrorCode.LockPersistFailed => "lock",
        _ => null
    };

    private static ExtensionStreamingResponse ErrorForConfiguration(
        IEnumerable<ConfigurationError> errors,
        int fallbackStatus = 502)
    {
        var error = errors.FirstOrDefault();
        if (error is null)
        {
            return Error(fallbackStatus, "backend_unavailable", "The configuration operation failed.");
        }

        var (status, code) = error.Code switch
        {
            ConfigurationErrorCode.Validation => (422, "validation"),
            ConfigurationErrorCode.ConcurrencyConflict => (409, "conflict"),
            ConfigurationErrorCode.NotFound => (404, "not_found"),
            ConfigurationErrorCode.Unsupported => (403, "forbidden"),
            ConfigurationErrorCode.StorageUnavailable => (502, "backend_unavailable"),
            _ => (fallbackStatus, "backend_unavailable")
        };
        return Error(status, code, error.Message);
    }

    private static ExtensionStreamingResponse Error(int status, string code, string message) =>
        JsonResponse(status, new { error = new { code, message } });

    private static ExtensionStreamingResponse JsonResponse(int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        return new ExtensionStreamingResponse(status, JsonHeaders, new MemoryStream(bytes, writable: false));
    }

    private static async ValueTask<BodyReadResult> ReadBodyAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        await using var body = new MemoryStream(capacity: 4096);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (body.Length + read > MaximumRequestBodyBytes)
            {
                return new BodyReadResult(Array.Empty<byte>(), true);
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return new BodyReadResult(body.ToArray(), false);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record BootstrapKeyRequest([property: JsonPropertyName("apiKey")] string? ApiKey);

    private sealed record ConfigRequest([property: JsonPropertyName("yaml")] string? Yaml);

    private sealed record BodyReadResult(byte[] Bytes, bool TooLarge);
}
