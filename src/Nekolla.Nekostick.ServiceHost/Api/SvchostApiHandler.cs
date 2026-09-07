using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.ServiceHost.Compose;
using Nekolla.Nekostick.ServiceHost.Settings;
using Nekolla.Nekostick.ServiceHost.Sync;

namespace Nekolla.Nekostick.ServiceHost.Api;

/// <summary>Serves the JSON management API under <c>/svchost/api</c>.</summary>
public sealed partial class SvchostApiHandler : IExtensionStreamingHandler, IDisposable
{
    public const string StableHandlerId = "nekostick.svchost.api";
    public const int MaximumRequestBodyBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly string ExtensionVersion =
        typeof(SvchostApiHandler).Assembly.GetName().Version?.ToString() ?? "unknown";
    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> JsonHeaders =
        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = new[] { "application/json; charset=utf-8" }
        };

    private readonly ApiKeyService _apiKeyService;
    private readonly SettingsStore _settingsStore;
    private readonly ComposeFileParser _composeFileParser;
    private readonly Reconciler _reconciler;
    private readonly IExtensionHostBridge13 _bridge;
    private readonly Func<IEnumerable<Guid>, IEnumerable<Guid>, CancellationToken, ValueTask<SyncReport>> _reconcile;
    private readonly Action<SyncReport>? _syncObserver;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _reportGate = new();
    private readonly Dictionary<string, SyncReport> _lastReports = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    public SvchostApiHandler(
        ApiKeyService apiKeyService,
        SettingsStore settingsStore,
        ComposeFileParser composeFileParser,
        Reconciler reconciler,
        IExtensionHostBridge13 bridge,
        Func<IEnumerable<Guid>, IEnumerable<Guid>, CancellationToken, ValueTask<SyncReport>>? reconcile = null,
        Action<SyncReport>? syncObserver = null)
    {
        _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _composeFileParser = composeFileParser ?? throw new ArgumentNullException(nameof(composeFileParser));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _reconcile = reconcile ?? ((services, routes, cancellationToken) =>
            _reconciler.ReconcileAsync(services, routes, cancellationToken));
        _syncObserver = syncObserver;
    }

    /// <inheritdoc />
    public string HandlerId => StableHandlerId;

    /// <summary>Cancels request-owned operations during extension shutdown.</summary>
    public void Cancel() => _lifetimeCancellation.Cancel();

    /// <summary>Releases the request lifetime cancellation source.</summary>
    public void Dispose() => _lifetimeCancellation.Dispose();

    /// <inheritdoc />
    public async ValueTask<ExtensionStreamingResponse> HandleStreamingAsync(
        ExtensionStreamingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var ct = linkedCancellation.Token;

        try
        {
            if (IsStatusRequest(request))
            {
                return await HandleStatusAsync(ct).ConfigureAwait(false);
            }

            if (!await IsAuthorizedAsync(request, ct).ConfigureAwait(false))
            {
                return Error(401, "unauthorized", "A valid X-Api-Key header is required.");
            }

            var segments = ParseSegments(request.Path);
            if (segments is null)
            {
                return Error(404, "not_found", "The API endpoint was not found.");
            }

            if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                !HasJsonPayload(request.Method, segments))
            {
                var ignoredBody = await ReadBodyAsync(request.BodyStream, ct).ConfigureAwait(false);
                if (ignoredBody.TooLarge)
                {
                    return Error(413, "request_too_large", "The request body exceeds the 1 MiB limit.");
                }
            }

            if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                segments.Length == 2 &&
                segments[0].Equals("bootstrap", StringComparison.Ordinal) &&
                segments[1].Equals("key", StringComparison.Ordinal))
            {
                return await HandleBootstrapKeyAsync(request.BodyStream, ct).ConfigureAwait(false);
            }

            if (segments.Length == 1 && segments[0].Equals("configs", StringComparison.Ordinal))
            {
                return request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    ? await HandleListConfigsAsync(ct).ConfigureAwait(false)
                    : Error(404, "not_found", "The API endpoint was not found.");
            }

            if (segments.Length == 2 && segments[0].Equals("configs", StringComparison.Ordinal))
            {
                return await HandleConfigAsync(request.Method, segments[1], request.BodyStream, ct).ConfigureAwait(false);
            }

            if (segments.Length == 3 &&
                segments[0].Equals("configs", StringComparison.Ordinal) &&
                segments[2].Equals("sync", StringComparison.Ordinal))
            {
                return request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                    ? await HandleSyncAsync(segments[1], ct).ConfigureAwait(false)
                    : Error(404, "not_found", "The API endpoint was not found.");
            }

            if (segments.Length == 1 && segments[0].Equals("services", StringComparison.Ordinal))
            {
                return request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    ? await HandleServicesAsync(ct).ConfigureAwait(false)
                    : Error(404, "not_found", "The API endpoint was not found.");
            }

            if (segments.Length == 4 && segments[0].Equals("services", StringComparison.Ordinal))
            {
                return request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                    ? await HandleServiceActionAsync(segments[1], segments[2], segments[3], ct).ConfigureAwait(false)
                    : Error(404, "not_found", "The API endpoint was not found.");
            }

            return Error(404, "not_found", "The API endpoint was not found.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Error(502, "backend_unavailable", "The API operation could not be completed.");
        }
    }

    private async ValueTask<SyncReport> ReconcileAndRememberAsync(
        IEnumerable<Guid> serviceIds,
        IEnumerable<Guid> routeIds,
        CancellationToken cancellationToken)
    {
        SyncReport report;
        try
        {
            report = await _reconcile(serviceIds, routeIds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            report = new SyncReport(
                false,
                !string.IsNullOrWhiteSpace(_bridge.DataDirectory),
                DateTimeOffset.UtcNow,
                ImmutableArray<ServiceSyncReport>.Empty,
                null,
                exception.Message)
            {
                FailureCode = SyncErrorCode.ReconcileFailed
            };
        }

        RememberReport(report);
        return report;
    }

    private void RememberReport(SyncReport report)
    {
        foreach (var group in report.Services.GroupBy(service => service.ConfigName, StringComparer.Ordinal))
        {
            var scopedReport = report with { Services = group.ToImmutableArray() };
            lock (_reportGate)
            {
                _lastReports[group.Key] = scopedReport;
            }
        }

        _syncObserver?.Invoke(report);
    }
    private SyncReport? GetLastReport(string configName)
    {
        lock (_reportGate)
        {
            return _lastReports.TryGetValue(configName, out var report) ? report : null;
        }
    }

    private async ValueTask<(SvchostSettings? Settings, long Version, ExtensionStreamingResponse? Error)> ReadSettingsAsync(
        CancellationToken cancellationToken)
    {
        var read = await _settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return (null, 0, ErrorForConfiguration(read.Errors));
        }

        if (read.Value!.Settings is null)
        {
            return (null, read.Value.Version, Error(502, "backend_unavailable", "Extension settings are not initialized."));
        }

        return (read.Value.Settings, read.Value.Version, null);
    }

    private async ValueTask<bool> IsAuthorizedAsync(
        ExtensionStreamingRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue("X-Api-Key", out var values) || values.IsDefaultOrEmpty)
        {
            return false;
        }

        return await _apiKeyService.VerifyAsync(values[0], cancellationToken).ConfigureAwait(false);
    }

    private static bool IsStatusRequest(ExtensionStreamingRequest request) =>
        request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
        request.Path.Equals("/svchost/api/status", StringComparison.Ordinal);

    private static string[]? ParseSegments(string path)
    {
        const string prefix = "/svchost/api";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
            (path.Length > prefix.Length && path[prefix.Length] != '/'))
        {
            return null;
        }

        if (path.Length == prefix.Length)
        {
            return Array.Empty<string>();
        }

        return path[prefix.Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Uri.UnescapeDataString(segment))
            .ToArray();
    }
    private static bool HasJsonPayload(string method, string[] segments) =>
        (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
         segments.Length == 2 &&
         segments[0].Equals("bootstrap", StringComparison.Ordinal) &&
         segments[1].Equals("key", StringComparison.Ordinal)) ||
        (method.Equals("PUT", StringComparison.OrdinalIgnoreCase) &&
         segments.Length == 2 &&
         segments[0].Equals("configs", StringComparison.Ordinal));


    private static bool IsValidName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 63 &&
        IsNameCharacter(value[0], allowHyphen: false) &&
        value.Skip(1).All(character => IsNameCharacter(character, allowHyphen: true));

    private static bool IsNameCharacter(char character, bool allowHyphen) =>
        character is >= 'a' and <= 'z' ||
        character is >= '0' and <= '9' ||
        allowHyphen && character == '-';




}
