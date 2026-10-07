using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class SourceResolverUrlTests
{
    private const string FirstUrl = "https://example.com/api-v1";
    private const string ChangedUrl = "https://example.com/api-v2";

    [Fact]
    public async Task Resolve_url_refetching_same_content_preserves_FetchedAt_and_source_change_updates_it()
    {
        var root = CreateTempDirectory();
        var requests = new List<string>();
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                requests.Add(url);
                var content = url == FirstUrl ? "version-one" : "version-two";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content))
                });
            }));
            var resolver = new SourceResolver(client, retryCount: 0);
            var dataDirectory = Path.Combine(root, "data");
            var source = new ComposeSource(FirstUrl, null);
            var first = await resolver.ResolveAsync(dataDirectory, "demo", "api", source, null);

            Assert.True(first.Succeeded, first.Error);
            var previousFetchedAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var firstSource = first.Source!;
            firstSource.FetchedAt = previousFetchedAt;
            File.Delete(first.ArtifactPath!);

            var refetched = await resolver.ResolveAsync(dataDirectory, "demo", "api", source, firstSource);

            Assert.True(refetched.Succeeded, refetched.Error);
            var refetchedSource = refetched.Source!;
            Assert.False(refetched.Reused);
            Assert.Equal(firstSource.Sha256, refetchedSource.Sha256);
            Assert.Equal(previousFetchedAt, refetchedSource.FetchedAt);
            refetchedSource.FetchedAt = previousFetchedAt;

            var changed = await resolver.ResolveAsync(
                dataDirectory,
                "demo",
                "api",
                new ComposeSource(ChangedUrl, null),
                refetchedSource);

            Assert.True(changed.Succeeded, changed.Error);
            var changedSource = changed.Source!;
            Assert.False(changed.Reused);
            Assert.NotEqual(refetchedSource.Sha256, changedSource.Sha256);
            Assert.NotEqual(previousFetchedAt, changedSource.FetchedAt);
            Assert.Equal(FirstUrl, firstSource.Url);
            Assert.Equal(ChangedUrl, changedSource.Url);
            Assert.Equal(3, requests.Count);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_http_url_accepts_declared_sha256()
    {
        const string url = "http://example.com/api";
        var content = Encoding.UTF8.GetBytes("http-source");
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var root = CreateTempDirectory();
        var requests = new List<string>();
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                requests.Add(request.RequestUri!.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                });
            }));
            var resolver = new SourceResolver(client, retryCount: 0);
            var result = await resolver.ResolveAsync(
                Path.Combine(root, "data"),
                "demo",
                "api",
                new ComposeSource(url, null, sha256),
                null);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(url, Assert.Single(requests));
            Assert.Equal(sha256, result.Source!.Sha256);
            Assert.Equal(content, await File.ReadAllBytesAsync(result.ArtifactPath!));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_http_url_rejects_declared_sha256_mismatch()
    {
        const string url = "http://example.com/api";
        var content = Encoding.UTF8.GetBytes("unexpected-source");
        var declaredDigest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("expected-source")))
            .ToLowerInvariant();
        var root = CreateTempDirectory();
        var requests = new List<string>();
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                requests.Add(request.RequestUri!.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                });
            }));
            var resolver = new SourceResolver(client, retryCount: 0);
            var result = await resolver.ResolveAsync(
                Path.Combine(root, "data"),
                "demo",
                "api",
                new ComposeSource(url, null, declaredDigest),
                null);

            Assert.False(result.Succeeded);
            Assert.Equal("The downloaded source does not match the locked or declared sha256.", result.Error);
            Assert.Equal(url, Assert.Single(requests));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Theory]
    [InlineData("ftp://example.com/api")]
    [InlineData("file:///tmp/api")]
    [InlineData("relative/api")]
    public async Task Resolve_url_rejects_unsupported_schemes_and_relative_values(string url)
    {
        var root = CreateTempDirectory();
        var requestCount = 0;
        try
        {
            using var client = new HttpClient(new StubHandler((_, _) =>
            {
                requestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                });
            }));
            var resolver = new SourceResolver(client, retryCount: 0);
            var result = await resolver.ResolveAsync(
                Path.Combine(root, "data"),
                "demo",
                "api",
                new ComposeSource(url, null),
                null);

            Assert.False(result.Succeeded);
            Assert.Equal("Source URLs must be absolute HTTP or HTTPS URLs.", result.Error);
            Assert.Equal(0, requestCount);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "nekostick-svchost-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
