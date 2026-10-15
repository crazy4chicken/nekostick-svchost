using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync.Releases;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class GitHubAppAuthTests
{
    private const long AppId = 123456;

    [Fact]
    public async Task GetInstallationToken_uses_configured_installation_id_and_caches_tokens()
    {
        var mintRequests = new List<HttpRequestMessage>();
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            mintRequests.Add(CloneRequest(request));
            return Task.FromResult(JsonResponse(new
            {
                token = "installation-token",
                expires_at = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
            }));
        }));
        using var auth = CreateAuth(client, installationId: 456);

        var first = await auth.GetInstallationTokenAsync("owner", "repo", CancellationToken.None);
        var second = await auth.GetInstallationTokenAsync("owner", "repo", CancellationToken.None);

        Assert.Equal("installation-token", first);
        Assert.Equal("installation-token", second);
        var mint = Assert.Single(mintRequests);
        Assert.Equal(HttpMethod.Post, mint.Method);
        Assert.Equal(
            "https://api.github.com/app/installations/456/access_tokens",
            mint.RequestUri!.ToString());
        AssertJwt(mint.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GetInstallationToken_resolves_the_installation_per_repository()
    {
        var requests = new List<HttpRequestMessage>();
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            if (request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/owner/repo/installation")
            {
                return Task.FromResult(JsonResponse(new { id = 789 }));
            }

            return Task.FromResult(JsonResponse(new
            {
                token = "installation-token",
                expires_at = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
            }));
        }));
        using var auth = CreateAuth(client);

        var first = await auth.GetInstallationTokenAsync("owner", "repo", CancellationToken.None);
        var second = await auth.GetInstallationTokenAsync("owner", "repo", CancellationToken.None);

        Assert.Equal("installation-token", first);
        Assert.Equal("installation-token", second);
        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("https://api.github.com/repos/owner/repo/installation", requests[0].RequestUri!.ToString());
        AssertJwt(requests[0].Headers.Authorization?.Parameter);
        Assert.Equal(
            "https://api.github.com/app/installations/789/access_tokens",
            requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task GetInstallationToken_renews_tokens_close_to_expiry()
    {
        var mintCount = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            mintCount++;
            return Task.FromResult(JsonResponse(new
            {
                token = $"token-{mintCount}",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O")
            }));
        }));
        using var auth = CreateAuth(client, installationId: 456);

        var first = await auth.GetInstallationTokenAsync("owner", "repo", CancellationToken.None);
        var second = await auth.GetInstallationTokenAsync("owner", "repo", CancellationToken.None);

        Assert.Equal("token-1", first);
        Assert.Equal("token-2", second);
        Assert.Equal(2, mintCount);
    }

    [Fact]
    public async Task GetInstallationToken_single_flights_concurrent_mints()
    {
        var mintCount = 0;
        using var client = new HttpClient(new StubHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref mintCount);
            await Task.Delay(50, cancellationToken);
            return JsonResponse(new
            {
                token = "installation-token",
                expires_at = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
            });
        }));
        using var auth = CreateAuth(client, installationId: 456);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => auth.GetInstallationTokenAsync("owner", "repo", CancellationToken.None)));

        Assert.All(tokens, token => Assert.Equal("installation-token", token));
        Assert.Equal(1, mintCount);
    }

    [Fact]
    public async Task GetInstallationToken_reports_installation_lookup_failures()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Not Found")
            })));
        using var auth = CreateAuth(client);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => auth.GetInstallationTokenAsync("owner", "repo", CancellationToken.None));

        Assert.Contains("owner/repo", exception.Message, StringComparison.Ordinal);
        Assert.Contains("404", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_rejects_invalid_private_keys()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            throw new InvalidOperationException("No request expected.")));

        Assert.Throws<ArgumentException>(() => new GitHubAppAuth(
            client,
            new GitHubAppSettings { AppId = AppId, PrivateKey = "not-a-pem" }));
    }

    private static GitHubAppAuth CreateAuth(HttpClient client, long? installationId = null) =>
        new(client, new GitHubAppSettings
        {
            AppId = AppId,
            InstallationId = installationId,
            PrivateKey = CreatePrivateKeyPem()
        });

    private static string CreatePrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static void AssertJwt(string? jwt)
    {
        Assert.False(string.IsNullOrEmpty(jwt));
        var segments = jwt!.Split('.');
        Assert.Equal(3, segments.Length);
        using var header = JsonDocument.Parse(DecodeBase64Url(segments[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        using var payload = JsonDocument.Parse(DecodeBase64Url(segments[1]));
        Assert.Equal(AppId, payload.RootElement.GetProperty("iss").GetInt64());
        var iat = payload.RootElement.GetProperty("iat").GetInt64();
        var exp = payload.RootElement.GetProperty("exp").GetInt64();
        Assert.Equal(600, exp - iat);
    }

    private static byte[] DecodeBase64Url(string segment)
    {
        var base64 = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Headers.Authorization is not null)
        {
            clone.Headers.TryAddWithoutValidation("Authorization", request.Headers.Authorization.ToString());
        }

        return clone;
    }

    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
