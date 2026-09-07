using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Nekolla.Nekostick.ServiceHost.Compose;

namespace Nekolla.Nekostick.ServiceHost.Sync;

/// <summary>Describes the result of resolving one source into the data directory.</summary>
public sealed record SourceResolutionResult(
    bool Succeeded,
    string? ArtifactPath,
    LockSource? Source,
    bool Reused,
    string? Error)
{
    /// <summary>Creates a successful resolution result.</summary>
    public static SourceResolutionResult Success(string artifactPath, LockSource source, bool reused) =>
        new(true, artifactPath, source, reused, null);

    /// <summary>Creates a failed resolution result.</summary>
    public static SourceResolutionResult Failure(string error) =>
        new(false, null, null, false, error);
}

/// <summary>Resolves HTTPS and local executable sources into reproducible artifacts.</summary>
public sealed partial class SourceResolver
{
    /// <summary>The default maximum artifact size.</summary>
    public const long DefaultMaximumArtifactBytes = 512L * 1024L * 1024L;

    /// <summary>The default request timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The default retry count after the initial download attempt.</summary>
    public const int DefaultRetryCount = 2;

    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;
    private readonly long _maximumArtifactBytes;
    private readonly int _retryCount;

    /// <summary>Creates a resolver using the shared pooled HttpClient by default.</summary>
    public SourceResolver(
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        long maximumArtifactBytes = DefaultMaximumArtifactBytes,
        int retryCount = DefaultRetryCount)
    {
        if (timeout.HasValue && timeout.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (maximumArtifactBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArtifactBytes));
        }

        if (retryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryCount));
        }

        _httpClient = httpClient ?? SharedHttpClient;
        _timeout = timeout ?? DefaultTimeout;
        _maximumArtifactBytes = maximumArtifactBytes;
        _retryCount = retryCount;
    }

    /// <summary>Resolves one source and atomically installs its artifact.</summary>
    public ValueTask<SourceResolutionResult> ResolveAsync(
        string dataDirectory,
        string configName,
        string serviceName,
        ComposeSource source,
        LockSource? previousLock,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(dataDirectory, configName, serviceName, source, previousLock, cancellationToken);

    private async ValueTask<SourceResolutionResult> ResolveCoreAsync(
        string dataDirectory,
        string configName,
        string serviceName,
        ComposeSource source,
        LockSource? previousLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return SourceResolutionResult.Failure("The extension data directory is unavailable.");
        }

        if (!IsValidName(configName) || !IsValidName(serviceName))
        {
            return SourceResolutionResult.Failure("Configuration and service names are invalid.");
        }

        var configDirectory = Path.Combine(
            Path.GetFullPath(dataDirectory),
            "svchost",
            configName);
        var artifactDirectory = Path.Combine(configDirectory, "artifacts");
        var temporaryDirectory = Path.Combine(configDirectory, "tmp");
        var artifactPath = Path.Combine(artifactDirectory, serviceName);
        Directory.CreateDirectory(artifactDirectory);
        Directory.CreateDirectory(temporaryDirectory);

        if (source.Url is not null)
        {
            return await ResolveUrlAsync(
                    source,
                    previousLock,
                    artifactPath,
                    temporaryDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (source.Path is not null)
        {
            return await ResolvePathAsync(
                    source,
                    previousLock,
                    artifactPath,
                    temporaryDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return SourceResolutionResult.Failure("A source URL or path is required.");
    }

}
