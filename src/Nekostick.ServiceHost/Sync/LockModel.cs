using System.Text.Json.Serialization;

namespace Nekostick.ServiceHost.Sync;

/// <summary>Contains reproducibility locks for all services in one named configuration.</summary>
public sealed class LockModel
{
    /// <summary>Gets or sets service locks keyed by service name.</summary>
    [JsonPropertyName("services")]
    public Dictionary<string, LockServiceEntry> Services { get; set; } =
        new(StringComparer.Ordinal);
}

/// <summary>Stores one service identity, source lock, and route identities.</summary>
public sealed class LockServiceEntry
{
    /// <summary>Creates an empty lock entry for JSON materialization.</summary>
    public LockServiceEntry()
    {
    }

    /// <summary>Creates a complete lock entry.</summary>
    public LockServiceEntry(LockSource source, Guid serviceId, IEnumerable<Guid>? routeIds = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ServiceId = serviceId;
        RouteIds = (routeIds ?? Array.Empty<Guid>()).ToList();
    }

    /// <summary>Gets or sets the locked source metadata.</summary>
    [JsonPropertyName("source")]
    public LockSource Source { get; set; } = new();

    /// <summary>Gets or sets the stable host service ID.</summary>
    [JsonPropertyName("serviceId")]
    public Guid ServiceId { get; set; }

    /// <summary>Gets or sets stable route IDs associated with this service.</summary>
    [JsonPropertyName("routeIds")]
    public List<Guid> RouteIds { get; set; } = new();
}

/// <summary>Locks one URL, path, or release source, digest, size, and fetch time.</summary>
public sealed class LockSource
{
    /// <summary>Gets or sets the source kind, either url, path, or release.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the locked URL.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Gets or sets the locked local path.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Gets or sets the release provider key.</summary>
    [JsonPropertyName("providerKey")]
    public string? ProviderKey { get; set; }

    /// <summary>Gets or sets the raw provider-specific release spec.</summary>
    [JsonPropertyName("spec")]
    public string? Spec { get; set; }

    /// <summary>Gets or sets the resolved release tag.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    /// <summary>Gets or sets the version extracted from the release asset name.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Gets or sets the resolved release asset name.</summary>
    [JsonPropertyName("assetName")]
    public string? AssetName { get; set; }

    /// <summary>Gets or sets the lowercase SHA-256 digest.</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; } = string.Empty;

    /// <summary>Gets or sets the source byte length.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Gets or sets the UTC time at which this source was fetched or copied.</summary>
    [JsonPropertyName("fetchedAt")]
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>Returns true when this lock identifies the same URL source.</summary>
    public bool MatchesUrl(string url) =>
        string.Equals(Kind, "url", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Url, url, StringComparison.Ordinal);

    /// <summary>Returns true when this lock identifies the same local source path.</summary>
    public bool MatchesPath(string path) =>
        string.Equals(Kind, "path", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path, path, StringComparison.Ordinal);

    /// <summary>Returns true when this lock identifies the same provider release source.</summary>
    public bool MatchesRelease(string providerKey, string spec) =>
        string.Equals(Kind, "release", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ProviderKey, providerKey, StringComparison.Ordinal) &&
        string.Equals(Spec, spec, StringComparison.Ordinal);
}
