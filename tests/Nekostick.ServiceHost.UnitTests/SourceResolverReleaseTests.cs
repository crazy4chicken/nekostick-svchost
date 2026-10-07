using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class SourceResolverReleaseTests
{
    private const string ApiUrl = "https://api.github.com/repos/owner/repo/releases?per_page=100";
    private static string OfficialDownloadUrl =>
        $"https://github.com/owner/repo/releases/download/v1.2.3/api_v1.2.3_{CurrentArch}.zip";

    [Fact]
    public async Task Resolve_release_falls_through_a_failed_mirror_to_the_next_mirror()
    {
        var root = CreateTempDirectory();
        var requests = new List<string>();
        var zip = CreateZip(("api", "release-binary"), ("nested/readme.txt", "whole archive"));
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                requests.Add(url);
                if (url == ApiUrl)
                {
                    return Task.FromResult(JsonResponse(CreateReleaseJson()));
                }

                if (url.StartsWith("https://mirror-one/", StringComparison.Ordinal))
                {
                    return Task.FromResult(Response(HttpStatusCode.NotFound));
                }

                if (url.StartsWith("https://mirror-two/", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(zip)
                    });
                }

                return Task.FromResult(Response(HttpStatusCode.NotFound));
            }));
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await ResolveAsync(
                resolver,
                Path.Combine(root, "data"),
                CreateSource(),
                null,
                CreateProviderSettings("https://mirror-one/", "https://mirror-two/"));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains(requests, url => url.StartsWith("https://mirror-one/", StringComparison.Ordinal));
            Assert.Contains(requests, url => url.StartsWith("https://mirror-two/", StringComparison.Ordinal));
            Assert.DoesNotContain(requests, url => url == OfficialDownloadUrl);
            Assert.Equal("release-binary", await File.ReadAllTextAsync(result.ArtifactPath!));
            Assert.Equal("whole archive", await File.ReadAllTextAsync(
                Path.Combine(Path.GetDirectoryName(result.ArtifactPath!)!, "nested", "readme.txt")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_tries_official_asset_after_all_mirrors_fail()
    {
        var root = CreateTempDirectory();
        var assetRequests = new List<string>();
        var zip = CreateZip(("api", "official-binary"));
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url == ApiUrl)
                {
                    return Task.FromResult(JsonResponse(CreateReleaseJson()));
                }

                assetRequests.Add(url);
                return Task.FromResult(url == OfficialDownloadUrl
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }
                    : Response(HttpStatusCode.NotFound));
            }));
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await ResolveAsync(
                resolver,
                Path.Combine(root, "data"),
                CreateSource(),
                null,
                CreateProviderSettings("https://mirror-one/", "https://mirror-two/"));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(
                [
                    string.Concat("https://mirror-one/", OfficialDownloadUrl),
                    string.Concat("https://mirror-two/", OfficialDownloadUrl),
                    OfficialDownloadUrl
                ],
                assetRequests);
            Assert.Equal("official-binary", await File.ReadAllTextAsync(result.ArtifactPath!));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_uses_the_next_mirror_after_a_sha256_mismatch()
    {
        var root = CreateTempDirectory();
        var assetRequests = new List<string>();
        var expectedZip = CreateZip(("api", "verified-binary"));
        var incorrectZip = CreateZip(("api", "incorrect-binary"));
        var expectedDigest = Convert.ToHexString(SHA256.HashData(expectedZip)).ToLowerInvariant();
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url == ApiUrl)
                {
                    return Task.FromResult(JsonResponse(CreateReleaseJson()));
                }

                assetRequests.Add(url);
                var content = url.StartsWith("https://mirror-one/", StringComparison.Ordinal)
                    ? incorrectZip
                    : expectedZip;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                });
            }));
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await ResolveAsync(
                resolver,
                Path.Combine(root, "data"),
                CreateSource(expectedDigest),
                null,
                CreateProviderSettings("https://mirror-one/", "https://mirror-two/"));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(2, assetRequests.Count);
            Assert.StartsWith("https://mirror-two/", assetRequests[1], StringComparison.Ordinal);
            Assert.Equal("verified-binary", await File.ReadAllTextAsync(result.ArtifactPath!));
            Assert.Equal(expectedDigest, result.Source!.Sha256);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_uses_the_github_asset_digest_when_compose_sha256_is_omitted()
    {
        var root = CreateTempDirectory();
        var assetRequests = new List<string>();
        var expectedZip = CreateZip(("api", "verified-binary"));
        var incorrectZip = CreateZip(("api", "incorrect-binary"));
        var expectedDigest = Convert.ToHexString(SHA256.HashData(expectedZip)).ToLowerInvariant();
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url == ApiUrl)
                {
                    return Task.FromResult(JsonResponse(CreateReleaseJson(digest: $"sha256:{expectedDigest}")));
                }

                assetRequests.Add(url);
                var content = url.StartsWith("https://mirror-one/", StringComparison.Ordinal)
                    ? incorrectZip
                    : expectedZip;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                });
            }));
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await ResolveAsync(
                resolver,
                Path.Combine(root, "data"),
                CreateSource(),
                null,
                CreateProviderSettings("https://mirror-one/"));

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(2, assetRequests.Count);
            Assert.Equal(OfficialDownloadUrl, assetRequests[1]);
            Assert.Equal("verified-binary", await File.ReadAllTextAsync(result.ArtifactPath!));
            Assert.Equal(expectedDigest, result.Source!.Sha256);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_reports_every_failed_candidate()
    {
        var root = CreateTempDirectory();
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                return Task.FromResult(url == ApiUrl
                    ? JsonResponse(CreateReleaseJson())
                    : Response(HttpStatusCode.NotFound));
            }));
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await ResolveAsync(
                resolver,
                Path.Combine(root, "data"),
                CreateSource(),
                null,
                CreateProviderSettings("https://mirror-one/", "https://mirror-two/"));

            Assert.False(result.Succeeded);
            Assert.Contains("All release asset download candidates failed", result.Error, StringComparison.Ordinal);
            Assert.Contains("https://mirror-one/", result.Error, StringComparison.Ordinal);
            Assert.Contains("https://mirror-two/", result.Error, StringComparison.Ordinal);
            Assert.Contains(OfficialDownloadUrl, result.Error, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_extracts_the_entire_archive_and_sets_executable_bit()
    {
        var root = CreateTempDirectory();
        var zip = CreateZip(("api", "release-binary"), ("nested/config.json", "{\"mode\":\"complete\"}"));
        try
        {
            using var client = CreateSingleAssetClient(zip);
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await resolver.ResolveAsync(
                Path.Combine(root, "data"),
                "demo",
                "api",
                CreateSource(),
                null);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("release-binary", await File.ReadAllTextAsync(result.ArtifactPath!));
            Assert.Equal("{\"mode\":\"complete\"}", await File.ReadAllTextAsync(
                Path.Combine(Path.GetDirectoryName(result.ArtifactPath!)!, "nested", "config.json")));
            Assert.Equal("release", result.Source!.Kind);
            Assert.Equal("github", result.Source.ProviderKey);
            Assert.Equal("owner/repo@v1.2.3", result.Source.Spec);
            Assert.Equal("v1.2.3", result.Source.Tag);
            Assert.Equal("v1.2.3", result.Source.Version);
            Assert.Equal($"api_v1.2.3_{CurrentArch}.zip", result.Source.AssetName);
            Assert.False(string.IsNullOrEmpty(result.Source.Sha256));
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(result.ArtifactPath!);
                Assert.True(mode.HasFlag(UnixFileMode.UserExecute), $"artifact must be executable, actual mode: {mode}");
            }
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_rejects_zip_slip_entries()
    {
        var root = CreateTempDirectory();
        var zip = CreateZip(("api", "release-binary"), ("../outside.txt", "escaped"));
        var dataDirectory = Path.Combine(root, "data");
        try
        {
            using var client = CreateSingleAssetClient(zip);
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await resolver.ResolveAsync(dataDirectory, "demo", "api", CreateSource(), null);

            Assert.False(result.Succeeded);
            Assert.Contains("escapes the extraction directory", result.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(dataDirectory, "svchost", "demo", "tmp", "outside.txt")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_requires_an_exact_root_executable_name()
    {
        var root = CreateTempDirectory();
        var zip = CreateZip(("API", "release-binary"));
        try
        {
            using var client = CreateSingleAssetClient(zip);
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await resolver.ResolveAsync(Path.Combine(root, "data"), "demo", "api", CreateSource(), null);

            Assert.False(result.Succeeded);
            Assert.Contains("does not contain an executable named 'api' at its extraction root", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_fails_when_root_executable_is_missing()
    {
        var root = CreateTempDirectory();
        var zip = CreateZip(("nested/api", "not-at-root"));
        try
        {
            using var client = CreateSingleAssetClient(zip);
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await resolver.ResolveAsync(Path.Combine(root, "data"), "demo", "api", CreateSource(), null);

            Assert.False(result.Succeeded);
            Assert.Contains("does not contain an executable named 'api' at its extraction root", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_reuses_an_existing_artifact_for_an_unchanged_identity()
    {
        var root = CreateTempDirectory();
        var assetDownloadCount = 0;
        var zip = CreateZip(("api", "release-binary"));
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url == ApiUrl)
                {
                    return Task.FromResult(JsonResponse(CreateReleaseJson()));
                }

                assetDownloadCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zip)
                });
            }));
            var resolver = new SourceResolver(client, retryCount: 0);
            var dataDirectory = Path.Combine(root, "data");
            var first = await resolver.ResolveAsync(dataDirectory, "demo", "api", CreateSource(), null);
            var second = await resolver.ResolveAsync(dataDirectory, "demo", "api", CreateSource(), first.Source);

            Assert.True(first.Succeeded, first.Error);
            Assert.True(second.Succeeded, second.Error);
            Assert.True(second.Reused);
            Assert.Equal(1, assetDownloadCount);
            Assert.Equal(first.Source!.Sha256, second.Source!.Sha256);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_refetching_same_content_preserves_FetchedAt()
    {
        var root = CreateTempDirectory();
        var assetDownloadCount = 0;
        var zip = CreateZip(("api", "release-binary"));
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url == ApiUrl)
                {
                    return Task.FromResult(JsonResponse(CreateReleaseJson()));
                }

                assetDownloadCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zip)
                });
            }));
            var resolver = new SourceResolver(client, retryCount: 0);
            var dataDirectory = Path.Combine(root, "data");
            var first = await resolver.ResolveAsync(dataDirectory, "demo", "api", CreateSource(), null);

            Assert.True(first.Succeeded, first.Error);
            var firstSource = first.Source!;
            var previousFetchedAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            firstSource.FetchedAt = previousFetchedAt;
            Directory.Delete(Path.GetDirectoryName(first.ArtifactPath!)!, recursive: true);
            var second = await resolver.ResolveAsync(dataDirectory, "demo", "api", CreateSource(), firstSource);

            Assert.True(second.Succeeded, second.Error);
            var secondSource = second.Source!;
            Assert.False(second.Reused);
            Assert.Equal(2, assetDownloadCount);
            Assert.Equal(firstSource.Sha256, secondSource.Sha256);
            Assert.Equal(previousFetchedAt, secondSource.FetchedAt);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_redownloads_when_the_resolved_identity_changes()
    {
        var root = CreateTempDirectory();
        var apiRequestCount = 0;
        var firstUrl = OfficialDownloadUrl;
        var secondUrl = $"https://github.com/owner/repo/releases/download/1.2.3/api_1.2.3_{CurrentArch}.zip";
        var zipRequests = new List<string>();
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url == ApiUrl)
                {
                    apiRequestCount++;
                    return Task.FromResult(JsonResponse(apiRequestCount == 1
                        ? CreateReleaseJson("v1.2.3", firstUrl)
                        : CreateReleaseJson("1.2.3", secondUrl, "1.2.3")));
                }

                zipRequests.Add(url);
                var contents = url == firstUrl
                    ? CreateZip(("api", "first-version"))
                    : CreateZip(("api", "second-version"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(contents)
                });
            }));
            var resolver = new SourceResolver(client, retryCount: 0);
            var dataDirectory = Path.Combine(root, "data");
            var first = await resolver.ResolveAsync(dataDirectory, "demo", "api", CreateSource(), null);
            Assert.True(first.Succeeded, first.Error);
            var firstSource = first.Source!;
            var previousFetchedAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            firstSource.FetchedAt = previousFetchedAt;
            var second = await resolver.ResolveAsync(dataDirectory, "demo", "api", CreateSource(), firstSource);

            Assert.True(second.Succeeded, second.Error);
            Assert.False(second.Reused);
            Assert.NotEqual(firstSource.Sha256, second.Source!.Sha256);
            Assert.Equal(2, zipRequests.Count);
            Assert.Equal("1.2.3", second.Source!.Tag);
            Assert.NotEqual(previousFetchedAt, second.Source!.FetchedAt);
            Assert.Equal("second-version", await File.ReadAllTextAsync(second.ArtifactPath!));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_propagates_provider_warnings()
    {
        var root = CreateTempDirectory();
        var zip = CreateZip(("api", "release-binary"));
        try
        {
            using var client = new HttpClient(new StubHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url == ApiUrl)
                {
                    return Task.FromResult(JsonResponse(CreateReleaseJson("stable", OfficialDownloadUrl, "1.0.0")));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zip)
                });
            }));
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await resolver.ResolveAsync(
                Path.Combine(root, "data"),
                "demo",
                "api",
                new ComposeSource(null, null, release: "github:owner/repo@stable"),
                null);

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("does not exactly match", Assert.Single(result.Warnings), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Resolve_release_fails_for_an_unregistered_provider()
    {
        var root = CreateTempDirectory();
        try
        {
            using var client = new HttpClient(new StubHandler((_, _) =>
                throw new InvalidOperationException("The API must not be requested for an unregistered provider.")));
            var resolver = new SourceResolver(client, retryCount: 0);

            var result = await resolver.ResolveAsync(
                Path.Combine(root, "data"),
                "demo",
                "api",
                new ComposeSource(null, null, release: "other:owner/repo@main"),
                null);

            Assert.False(result.Succeeded);
            Assert.Contains("other", result.Error, StringComparison.Ordinal);
            Assert.Contains("not registered", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static async Task<SourceResolutionResult> ResolveAsync(
        SourceResolver resolver,
        string dataDirectory,
        ComposeSource source,
        LockSource? previousLock,
        IReadOnlyDictionary<string, ReleaseProviderSettings> releaseProviderSettings) =>
        await resolver.ResolveAsync(
            dataDirectory,
            "demo",
            "api",
            source,
            previousLock,
            releaseProviderSettings,
            CancellationToken.None);

    private static ComposeSource CreateSource(string? sha256 = null) =>
        new(null, null, sha256, "github:owner/repo@v1.2.3");

    private static IReadOnlyDictionary<string, ReleaseProviderSettings> CreateProviderSettings(params string[] mirrors) =>
        new Dictionary<string, ReleaseProviderSettings>(StringComparer.Ordinal)
        {
            ["github"] = new() { Mirrors = mirrors.ToList() }
        };

    private static HttpClient CreateSingleAssetClient(byte[] zip)
    {
        return new HttpClient(new StubHandler((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(url == ApiUrl
                ? JsonResponse(CreateReleaseJson())
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        }));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Response(HttpStatusCode statusCode) => new(statusCode)
    {
        Content = new StringContent("not found")
    };

    private static string CreateReleaseJson(
        string tag = "v1.2.3",
        string? downloadUrl = null,
        string? version = null,
        string? digest = null)
    {
        downloadUrl ??= OfficialDownloadUrl;
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                draft = false,
                tag_name = tag,
                target_commitish = "main",
                prerelease = false,
                assets = new[]
                {
                    new
                    {
                        name = $"api_{version ?? tag}_{CurrentArch}.zip",
                        browser_download_url = downloadUrl,
                        digest = digest
                    }
                }
            }
        });
    }

    private static string CurrentArch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        Architecture.X86 => "x86",
        _ => "unsupported"
    };

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
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
