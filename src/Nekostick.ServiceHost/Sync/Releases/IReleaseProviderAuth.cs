namespace Nekostick.ServiceHost.Sync.Releases;

/// <summary>Supplies bearer tokens for authenticated release provider requests.</summary>
public interface IReleaseProviderAuth
{
    /// <summary>Resolves an access token for the given repository coordinates.</summary>
    Task<string> GetTokenAsync(string owner, string repository, CancellationToken cancellationToken);
}
