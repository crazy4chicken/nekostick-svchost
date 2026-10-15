using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nekostick.ServiceHost.Settings;

namespace Nekostick.ServiceHost.Sync.Releases;

/// <summary>Mints and caches GitHub App installation access tokens for authenticated release downloads.</summary>
public sealed class GitHubAppAuth : IReleaseProviderAuth, IDisposable
{
    private const string ApiRoot = "https://api.github.com";
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly GitHubAppSettings _settings;
    private readonly RSA _rsa;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _mintGate = new(1, 1);
    private readonly Dictionary<string, long> _installationIds = new(StringComparer.Ordinal);
    private readonly Dictionary<long, InstallationToken> _tokens = new();
    private bool _disposed;

    /// <summary>Creates an authenticator over the supplied HTTP client and App credentials.</summary>
    public GitHubAppAuth(HttpClient httpClient, GitHubAppSettings settings)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            throw new ArgumentException("The GitHub App private key is required.", nameof(settings));
        }

        _rsa = RSA.Create();
        try
        {
            _rsa.ImportFromPem(settings.PrivateKey);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            _rsa.Dispose();
            throw new ArgumentException("The GitHub App private key must be a PEM-encoded RSA private key.", nameof(settings));
        }
    }

    /// <summary>Resolves a valid installation access token for the given repository.</summary>
    public async Task<string> GetInstallationTokenAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var installationId = await ResolveInstallationIdAsync(owner, repository, cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            if (_tokens.TryGetValue(installationId, out var cached) && IsUsable(cached))
            {
                return cached.Value;
            }
        }

        await _mintGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_tokens.TryGetValue(installationId, out var cached) && IsUsable(cached))
                {
                    return cached.Value;
                }
            }

            var minted = await MintInstallationTokenAsync(installationId, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _tokens[installationId] = minted;
            }

            return minted.Value;
        }
        finally
        {
            _mintGate.Release();
        }
    }

    /// <inheritdoc />
    Task<string> IReleaseProviderAuth.GetTokenAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken) =>
        GetInstallationTokenAsync(owner, repository, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _rsa.Dispose();
        _mintGate.Dispose();
    }

    private static bool IsUsable(InstallationToken token) =>
        token.ExpiresAt > DateTimeOffset.UtcNow + TokenRefreshMargin;

    private async Task<long> ResolveInstallationIdAsync(string owner, string repository, CancellationToken cancellationToken)
    {
        if (_settings.InstallationId is { } configured)
        {
            return configured;
        }

        var key = $"{owner}/{repository}";
        lock (_sync)
        {
            if (_installationIds.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        await _mintGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_installationIds.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            using var request = CreateApiRequest(
                HttpMethod.Get,
                $"{ApiRoot}/repos/{owner}/{repository}/installation");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Resolving the GitHub App installation for repository '{key}' failed with HTTP {(int)response.StatusCode} ({response.StatusCode}). " +
                    "Verify the App ID, private key, and that the App is installed on the repository with 'Contents: read' permission.");
            }

            using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.Number)
            {
                throw new HttpRequestException(
                    $"Resolving the GitHub App installation for repository '{key}' returned an invalid response.");
            }

            var installationId = idElement.GetInt64();
            lock (_sync)
            {
                _installationIds[key] = installationId;
            }

            return installationId;
        }
        finally
        {
            _mintGate.Release();
        }
    }

    private async Task<InstallationToken> MintInstallationTokenAsync(long installationId, CancellationToken cancellationToken)
    {
        using var request = CreateApiRequest(
            HttpMethod.Post,
            $"{ApiRoot}/app/installations/{installationId}/access_tokens");
        request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Minting a GitHub App installation token for installation '{installationId}' failed with HTTP {(int)response.StatusCode} ({response.StatusCode}). " +
                "Verify the App ID and private key.");
        }

        using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("token", out var tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String)
        {
            throw new HttpRequestException(
                $"Minting a GitHub App installation token for installation '{installationId}' returned an invalid response.");
        }

        var expiresAt = document.RootElement.TryGetProperty("expires_at", out var expiresElement) &&
                        expiresElement.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(expiresElement.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow.AddHours(1);
        return new InstallationToken(tokenElement.GetString()!, expiresAt);
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt());
        request.Headers.UserAgent.ParseAdd("nekostick-svchost");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    private string CreateJwt()
    {
        // RSA.SignData thread-safety is implementation-defined; serialize JWT creation.
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var header = Base64UrlEncode("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"u8.ToArray());
            var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
                exp = now.AddMinutes(9).ToUnixTimeSeconds(),
                iss = _settings.AppId
            }));
            var unsigned = string.Concat(header, ".", payload);
            var signature = _rsa.SignData(
                Encoding.ASCII.GetBytes(unsigned),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return string.Concat(unsigned, ".", Base64UrlEncode(signature));
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record InstallationToken(string Value, DateTimeOffset ExpiresAt);
}
