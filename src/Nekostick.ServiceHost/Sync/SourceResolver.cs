using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync.Releases;

namespace Nekostick.ServiceHost.Sync;

/// <summary>Describes the result of resolving one source into the data directory.</summary>
public sealed record SourceResolutionResult(
    bool Succeeded,
    string? ArtifactPath,
    LockSource? Source,
    bool Reused,
    string? Error)
{
    /// <summary>Gets non-fatal warnings produced while resolving the source.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Creates a successful resolution result.</summary>
    public static SourceResolutionResult Success(
        string artifactPath,
        LockSource source,
        bool reused,
        IEnumerable<string>? warnings = null) =>
        new(true, artifactPath, source, reused, null)
        {
            Warnings = (warnings ?? Array.Empty<string>()).ToArray()
        };

    /// <summary>Creates a failed resolution result.</summary>
    public static SourceResolutionResult Failure(string error, IEnumerable<string>? warnings = null) =>
        new(false, null, null, false, error)
        {
            Warnings = (warnings ?? Array.Empty<string>()).ToArray()
        };
}

/// <summary>Resolves HTTP/HTTPS, local, and provider release sources into reproducible artifacts.</summary>
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
    private readonly ReleaseProviderRegistry _releaseProviders;

    /// <summary>Creates a resolver using the shared pooled HttpClient by default.</summary>
    public SourceResolver(
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        long maximumArtifactBytes = DefaultMaximumArtifactBytes,
        int retryCount = DefaultRetryCount,
        ReleaseProviderRegistry? releaseProviders = null)
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
        _releaseProviders = releaseProviders ?? new ReleaseProviderRegistry([new GitHubReleaseProvider(_httpClient)]);
    }

    /// <summary>Resolves one source and atomically installs its artifact.</summary>
    public ValueTask<SourceResolutionResult> ResolveAsync(
        string dataDirectory,
        string configName,
        string serviceName,
        ComposeSource source,
        LockSource? previousLock,
        CancellationToken cancellationToken = default) =>
        ResolveForConfigAsync(dataDirectory, configName, serviceName, source, previousLock, null, cancellationToken);

    /// <summary>Resolves one source using the current provider-specific settings.</summary>
    public ValueTask<SourceResolutionResult> ResolveAsync(
        string dataDirectory,
        string configName,
        string serviceName,
        ComposeSource source,
        LockSource? previousLock,
        IReadOnlyDictionary<string, ReleaseProviderSettings>? releaseProviderSettings,
        CancellationToken cancellationToken) =>
        ResolveForConfigAsync(
            dataDirectory,
            configName,
            serviceName,
            source,
            previousLock,
            releaseProviderSettings,
            cancellationToken);

    internal ValueTask<SourceResolutionResult> ResolveInServiceRootAsync(
        string serviceRootDirectory,
        string serviceName,
        ComposeSource source,
        LockSource? previousLock,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(serviceRootDirectory, serviceName, source, previousLock, null, cancellationToken);

    internal ValueTask<SourceResolutionResult> ResolveInServiceRootAsync(
        string serviceRootDirectory,
        string serviceName,
        ComposeSource source,
        LockSource? previousLock,
        IReadOnlyDictionary<string, ReleaseProviderSettings>? releaseProviderSettings,
        CancellationToken cancellationToken) =>
        ResolveCoreAsync(serviceRootDirectory, serviceName, source, previousLock, releaseProviderSettings, cancellationToken);

    internal static LockSource PreserveUnchangedSourceLock(LockSource? existing, LockSource candidate)
    {
        if (existing is null ||
            !string.Equals(existing.Kind, candidate.Kind, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.Url, candidate.Url, StringComparison.Ordinal) ||
            !string.Equals(existing.Path, candidate.Path, StringComparison.Ordinal) ||
            !string.Equals(existing.ProviderKey, candidate.ProviderKey, StringComparison.Ordinal) ||
            !string.Equals(existing.Spec, candidate.Spec, StringComparison.Ordinal) ||
            !string.Equals(existing.Tag, candidate.Tag, StringComparison.Ordinal) ||
            !string.Equals(existing.Version, candidate.Version, StringComparison.Ordinal) ||
            !string.Equals(existing.AssetName, candidate.AssetName, StringComparison.Ordinal) ||
            !string.Equals(existing.Sha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase) ||
            existing.Size != candidate.Size)
        {
            return candidate;
        }

        return existing;
    }

    private ValueTask<SourceResolutionResult> ResolveForConfigAsync(
        string dataDirectory,
        string configName,
        string serviceName,
        ComposeSource source,
        LockSource? previousLock,
        IReadOnlyDictionary<string, ReleaseProviderSettings>? releaseProviderSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return ValueTask.FromResult(SourceResolutionResult.Failure("The extension data directory is unavailable."));
        }

        if (!IsValidConfigName(configName) || !IsValidName(serviceName))
        {
            return ValueTask.FromResult(SourceResolutionResult.Failure("Configuration and service names are invalid."));
        }

        var serviceRootDirectory = ServiceRootPath.Resolve(dataDirectory, ComposeServiceScope.Document, configName);
        return ResolveInServiceRootAsync(
            serviceRootDirectory,
            serviceName,
            source,
            previousLock,
            releaseProviderSettings,
            cancellationToken);
    }

    private async ValueTask<SourceResolutionResult> ResolveCoreAsync(
        string serviceRootDirectory,
        string serviceName,
        ComposeSource source,
        LockSource? previousLock,
        IReadOnlyDictionary<string, ReleaseProviderSettings>? releaseProviderSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(serviceRootDirectory))
        {
            return SourceResolutionResult.Failure("The extension data directory is unavailable.");
        }

        if (!IsValidName(serviceName))
        {
            return SourceResolutionResult.Failure("Service names are invalid.");
        }

        var configDirectory = Path.GetFullPath(serviceRootDirectory);
        var serviceArtifactDirectory = Path.Combine(configDirectory, "artifacts", "sha256", serviceName);
        var temporaryDirectory = Path.Combine(configDirectory, "tmp");
        Directory.CreateDirectory(serviceArtifactDirectory);
        Directory.CreateDirectory(temporaryDirectory);

        if (source.Release is not null)
        {
            return await ResolveReleaseAsync(
                    source,
                    serviceName,
                    previousLock,
                    releaseProviderSettings,
                    _releaseProviders,
                    serviceArtifactDirectory,
                    temporaryDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (source.Url is not null)
        {
            return await ResolveUrlAsync(
                    source,
                    serviceName,
                    previousLock,
                    serviceArtifactDirectory,
                    temporaryDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (source.Path is not null)
        {
            return await ResolvePathAsync(
                    source,
                    serviceName,
                    previousLock,
                    serviceArtifactDirectory,
                    temporaryDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return SourceResolutionResult.Failure("A source URL, path, or release is required.");
    }

}
