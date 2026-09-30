namespace Nekostick.ServiceHost.Sync.Releases;

/// <summary>Resolves a provider-specific release source to a downloadable asset.</summary>
public interface IReleaseProvider
{
    /// <summary>Gets the stable provider key used by compose release sources.</summary>
    string Key { get; }

    /// <summary>Resolves a release and selects its service and architecture-specific asset.</summary>
    Task<ReleaseProviderResult> ResolveAsync(
        ReleaseSource source,
        string serviceId,
        string arch,
        CancellationToken cancellationToken);
}
