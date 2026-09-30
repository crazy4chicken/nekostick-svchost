namespace Nekostick.ServiceHost.Sync.Releases;

/// <summary>Indexes release providers by their stable provider key.</summary>
public sealed class ReleaseProviderRegistry
{
    private readonly Dictionary<string, IReleaseProvider> _providers;

    /// <summary>Creates a registry containing the supplied providers.</summary>
    public ReleaseProviderRegistry(IEnumerable<IReleaseProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = new Dictionary<string, IReleaseProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (string.IsNullOrWhiteSpace(provider.Key))
            {
                throw new ArgumentException("Release provider keys must not be empty.", nameof(providers));
            }

            if (!_providers.TryAdd(provider.Key, provider))
            {
                throw new ArgumentException($"Release provider key '{provider.Key}' is duplicated.", nameof(providers));
            }
        }
    }

    /// <summary>Attempts to retrieve a provider by its exact key.</summary>
    public bool TryGetProvider(string key, out IReleaseProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_providers.TryGetValue(key, out var found))
        {
            provider = found;
            return true;
        }

        provider = null;
        return false;
    }
}
