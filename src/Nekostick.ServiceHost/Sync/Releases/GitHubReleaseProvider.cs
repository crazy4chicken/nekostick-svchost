using System.Net.Http;
using System.Text.Json;

namespace Nekostick.ServiceHost.Sync.Releases;

/// <summary>Resolves GitHub releases through the unauthenticated REST API.</summary>
public sealed class GitHubReleaseProvider : IReleaseProvider
{
    private const string ApiRoot = "https://api.github.com";
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _httpClient;

    /// <summary>Creates a provider using the shared HTTP client unless one is supplied.</summary>
    public GitHubReleaseProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    /// <inheritdoc />
    public string Key => "github";

    /// <inheritdoc />
    public async Task<ReleaseProviderResult> ResolveAsync(
        ReleaseSource source,
        string serviceId,
        string arch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!TryParseSpec(source.Spec, out var owner, out var repository, out var reference) ||
            !string.Equals(reference, source.Ref, StringComparison.Ordinal))
        {
            return ReleaseProviderResult.Failure(
                "GitHub release specs must use the format '{owner}/{repo}@{ref}' with non-empty owner, repo, and ref values.");
        }

        if (string.IsNullOrEmpty(serviceId) || string.IsNullOrEmpty(arch))
        {
            return ReleaseProviderResult.Failure("A service ID and architecture are required to select a GitHub release asset.");
        }

        var repositoryName = $"{owner}/{repository}";
        var apiUrl = new Uri($"{ApiRoot}/repos/{owner}/{repository}/releases?per_page=100");
        List<GitHubRelease> releases;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.ParseAdd("nekostick-svchost");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ReleaseProviderResult.Failure(
                    $"GitHub API request for repository '{repositoryName}' failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ReleaseProviderResult.Failure(
                    $"GitHub API returned an invalid releases response for repository '{repositoryName}'.");
            }

            releases = ParseReleases(document.RootElement);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ReleaseProviderResult.Failure(
                $"GitHub API request for repository '{repositoryName}' timed out: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return ReleaseProviderResult.Failure(
                $"GitHub API request for repository '{repositoryName}' failed: {exception.Message}");
        }
        catch (IOException exception)
        {
            return ReleaseProviderResult.Failure(
                $"GitHub API response for repository '{repositoryName}' could not be read: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return ReleaseProviderResult.Failure(
                $"GitHub API returned invalid release JSON for repository '{repositoryName}': {exception.Message}");
        }

        var releaseRef = ReleaseRef.Classify(reference);
        var release = releases.FirstOrDefault(candidate => MatchesRef(candidate, releaseRef));
        if (release is null)
        {
            return ReleaseProviderResult.Failure(
                $"No GitHub release matched ref '{reference}' in repository '{repositoryName}'.");
        }

        var availableNames = release.Assets.Select(asset => asset.Name).ToArray();
        var prefix = string.Concat(serviceId, "_");
        var suffix = string.Concat("_", arch, ".zip");
        var namedAssets = new List<AssetCandidate>();
        foreach (var asset in release.Assets)
        {
            if (asset.Name.Length <= prefix.Length + suffix.Length ||
                !asset.Name.StartsWith(prefix, StringComparison.Ordinal) ||
                !asset.Name.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var version = asset.Name.Substring(prefix.Length, asset.Name.Length - prefix.Length - suffix.Length);
            namedAssets.Add(new AssetCandidate(asset, version));
        }

        if (namedAssets.Count == 0)
        {
            return ReleaseProviderResult.Failure(
                $"No GitHub asset matching '{serviceId}_<version>_{arch}.zip' was found in release '{release.Tag}' for repository '{repositoryName}'. Available assets: {FormatAssetNames(availableNames)}");
        }

        var matchingAssets = namedAssets.Where(candidate => MatchesAssetVersion(candidate.Version, releaseRef)).ToList();
        if (matchingAssets.Count == 0)
        {
            var availableVersions = string.Join(", ", namedAssets.Select(candidate => candidate.Version));
            return ReleaseProviderResult.Failure(releaseRef.Kind switch
            {
                ReleaseRefKind.SemVer =>
                    $"GitHub asset version must equal SemVer ref '{reference}' in release '{release.Tag}' for repository '{repositoryName}'. Available asset versions: {availableVersions}.",
                ReleaseRefKind.CommitHash =>
                    $"GitHub asset version must start with commit ref '{reference}' in release '{release.Tag}' for repository '{repositoryName}'. Available asset versions: {availableVersions}.",
                _ => $"No matching GitHub asset was found in release '{release.Tag}' for repository '{repositoryName}'."
            });
        }

        var selected = matchingAssets
            .OrderBy(candidate => GetAssetPreference(candidate.Version, release.Tag, releaseRef))
            .First();
        if (string.IsNullOrWhiteSpace(selected.Asset.DownloadUrl))
        {
            return ReleaseProviderResult.Failure(
                $"GitHub asset '{selected.Asset.Name}' in repository '{repositoryName}' does not include a browser download URL.");
        }

        var warnings = releaseRef.Kind == ReleaseRefKind.Tag &&
                       !string.Equals(selected.Version, release.Tag, StringComparison.Ordinal)
            ? new[]
            {
                $"GitHub asset version '{selected.Version}' does not exactly match release tag '{release.Tag}'."
            }
            : Array.Empty<string>();
        return ReleaseProviderResult.Success(
            new ResolvedReleaseAsset(
                release.Tag,
                selected.Version,
                selected.Asset.Name,
                selected.Asset.DownloadUrl,
                selected.Asset.Sha256),
            warnings);
    }

    private static List<GitHubRelease> ParseReleases(JsonElement root)
    {
        var releases = new List<GitHubRelease>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || IsTrue(item, "draft"))
            {
                continue;
            }

            var tag = ReadString(item, "tag_name");
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }

            var assets = new List<GitHubAsset>();
            if (item.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assetsElement.EnumerateArray())
                {
                    if (assetElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = ReadString(assetElement, "name");
                    if (name is null)
                    {
                        continue;
                    }

                    assets.Add(new GitHubAsset(
                        name,
                        ReadString(assetElement, "browser_download_url"),
                        ReadSha256(ReadString(assetElement, "digest"))));
                }
            }

            releases.Add(new GitHubRelease(
                tag,
                ReadString(item, "target_commitish") ?? string.Empty,
                assets));
        }

        return releases;
    }

    private static bool TryParseSpec(
        string spec,
        out string owner,
        out string repository,
        out string reference)
    {
        owner = string.Empty;
        repository = string.Empty;
        reference = string.Empty;
        var separator = spec.IndexOf('@');
        if (separator <= 0 || separator == spec.Length - 1)
        {
            return false;
        }

        var repositoryPath = spec[..separator];
        var slash = repositoryPath.IndexOf('/');
        if (slash <= 0 || slash == repositoryPath.Length - 1 || repositoryPath.IndexOf('/', slash + 1) >= 0)
        {
            return false;
        }

        owner = repositoryPath[..slash];
        repository = repositoryPath[(slash + 1)..];
        reference = spec[(separator + 1)..];
        return IsRepositoryPart(owner) && IsRepositoryPart(repository) && !string.IsNullOrWhiteSpace(reference);
    }

    private static bool IsRepositoryPart(string value)
    {
        foreach (var character in value)
        {
            if (character is not (>= 'A' and <= 'Z') and
                not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and
                not '_' and
                not '.' and
                not '-')
            {
                return false;
            }
        }

        return value.Length > 0 && value != "." && value != "..";
    }

    private static bool MatchesRef(GitHubRelease release, ReleaseRef releaseRef) => releaseRef.Kind switch
    {
        ReleaseRefKind.SemVer =>
            releaseRef.Version is { } requestedVersion &&
            SemVersion.TryParse(release.Tag, out var taggedVersion) &&
            taggedVersion == requestedVersion,
        ReleaseRefKind.CommitHash => release.TargetCommitish.StartsWith(releaseRef.Value, StringComparison.OrdinalIgnoreCase),
        ReleaseRefKind.Tag => string.Equals(release.Tag, releaseRef.Value, StringComparison.Ordinal),
        _ => false
    };

    private static bool MatchesAssetVersion(string version, ReleaseRef releaseRef) => releaseRef.Kind switch
    {
        ReleaseRefKind.SemVer =>
            releaseRef.Version is { } requestedVersion &&
            SemVersion.TryParse(version, out var assetVersion) &&
            assetVersion == requestedVersion,
        ReleaseRefKind.CommitHash => version.StartsWith(releaseRef.Value, StringComparison.OrdinalIgnoreCase),
        ReleaseRefKind.Tag => true,
        _ => false
    };

    private static int GetAssetPreference(string version, string tag, ReleaseRef releaseRef)
    {
        if (string.Equals(version, tag, StringComparison.Ordinal))
        {
            return 0;
        }

        if (string.Equals(version, releaseRef.Value, StringComparison.Ordinal))
        {
            return 1;
        }

        if (SemVersion.TryParse(version, out var assetVersion) &&
            ((releaseRef.Version is { } requestedVersion && assetVersion == requestedVersion) ||
             (SemVersion.TryParse(tag, out var tagVersion) && assetVersion == tagVersion)))
        {
            return 2;
        }

        return 3;
    }

    private static string? ReadSha256(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = digest[prefix.Length..];
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            return null;
        }

        return value.ToLowerInvariant();
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsTrue(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private static string FormatAssetNames(IReadOnlyList<string> names) =>
        names.Count == 0 ? "none" : string.Join(", ", names);

    private sealed record GitHubRelease(string Tag, string TargetCommitish, List<GitHubAsset> Assets);

    private sealed record GitHubAsset(string Name, string? DownloadUrl, string? Sha256);

    private sealed record AssetCandidate(GitHubAsset Asset, string Version);
}
