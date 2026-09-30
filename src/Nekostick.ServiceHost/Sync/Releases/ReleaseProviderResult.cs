namespace Nekostick.ServiceHost.Sync.Releases;

/// <summary>Describes one release asset selected by a provider.</summary>
public sealed record ResolvedReleaseAsset(
    string Tag,
    string Version,
    string AssetName,
    string DownloadUrl,
    string? Sha256);

/// <summary>Contains a provider resolution result and any non-fatal warnings.</summary>
public sealed record ReleaseProviderResult(
    bool Succeeded,
    ResolvedReleaseAsset? Asset,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    /// <summary>Creates a successful provider result.</summary>
    public static ReleaseProviderResult Success(
        ResolvedReleaseAsset asset,
        IEnumerable<string>? warnings = null) =>
        new(true, asset, (warnings ?? Array.Empty<string>()).ToArray(), null);

    /// <summary>Creates a failed provider result.</summary>
    public static ReleaseProviderResult Failure(string error) =>
        new(false, null, Array.Empty<string>(), error);
}
