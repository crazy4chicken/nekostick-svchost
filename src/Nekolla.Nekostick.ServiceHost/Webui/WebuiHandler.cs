using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.ServiceHost.Webui;

/// <summary>Serves the embedded single-file WebUI under <c>/svchost</c>.</summary>
public sealed class WebuiHandler : IExtensionStreamingHandler
{
    public const string StableHandlerId = "nekostick.svchost.webui";
    public const string ManifestResourceName = "Nekolla.Nekostick.ServiceHost.webui.index.html";

    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> HtmlHeaders =
        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = new[] { "text/html; charset=utf-8" },
            ["Cache-Control"] = new[] { "no-cache" }
        };

    /// <inheritdoc />
    public string HandlerId => StableHandlerId;

    /// <inheritdoc />
    public ValueTask<ExtensionStreamingResponse> HandleStreamingAsync(
        ExtensionStreamingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(
                new ExtensionStreamingResponse(
                    405,
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Allow"] = new[] { "GET" },
                        ["Content-Type"] = new[] { "text/plain; charset=utf-8" }
                    },
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Method Not Allowed"), writable: false)));
        }

        if (!IsWebUiPath(request.Path))
        {
            return ValueTask.FromResult(
                new ExtensionStreamingResponse(
                    404,
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Content-Type"] = new[] { "text/plain; charset=utf-8" }
                    },
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Not Found"), writable: false)));
        }

        var stream = typeof(WebuiHandler).Assembly.GetManifestResourceStream(ManifestResourceName);
        if (stream is null)
        {
            return ValueTask.FromResult(
                new ExtensionStreamingResponse(
                    500,
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Content-Type"] = new[] { "text/plain; charset=utf-8" }
                    },
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes("WebUI resource is unavailable."), writable: false)));
        }

        return ValueTask.FromResult(new ExtensionStreamingResponse(200, HtmlHeaders, stream));
    }

    private static bool IsWebUiPath(string path) =>
        path.Equals("/svchost", StringComparison.Ordinal) ||
        (path.StartsWith("/svchost/", StringComparison.Ordinal) &&
         !path.Equals("/svchost/api", StringComparison.Ordinal) &&
         !path.StartsWith("/svchost/api/", StringComparison.Ordinal));
}
