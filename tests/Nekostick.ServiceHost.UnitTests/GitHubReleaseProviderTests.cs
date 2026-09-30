using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Nekostick.ServiceHost.Sync.Releases;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class GitHubReleaseProviderTests
{
    private const string OfficialDownloadUrl = "https://github.com/owner/repo/releases/download/v1.2.3/api_v1.2.3_x64.zip";

    [Fact]
    public async Task Resolve_semver_matches_release_tag_and_asset_with_optional_v_prefix()
    {
        var digest = new string('A', 64);
        var json = CreateReleases(
            new TestRelease("v1.2.3", "main", [new TestAsset("api_draft_x64.zip", OfficialDownloadUrl)], Draft: true),
            new TestRelease("v1.2.3", "main", [new TestAsset("api_v1.2.3_x64.zip", OfficialDownloadUrl, $"sha256:{digest}")]));

        var result = await ResolveAsync("1.2.3", json);

        Assert.True(result.Succeeded);
        Assert.Equal("v1.2.3", result.Asset!.Tag);
        Assert.Equal("v1.2.3", result.Asset.Version);
        Assert.Equal(new string('a', 64), result.Asset.Sha256);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Resolve_commit_hash_matches_target_commitish_prefix()
    {
        var json = CreateReleases(
            new TestRelease("nightly", "abcdef1234567890", [new TestAsset("api_abcdef123_x64.zip", OfficialDownloadUrl)]));

        var result = await ResolveAsync("abcdef1", json);

        Assert.True(result.Succeeded);
        Assert.Equal("nightly", result.Asset!.Tag);
        Assert.Equal("abcdef123", result.Asset.Version);
    }

    [Fact]
    public async Task Resolve_commit_asset_version_mismatch_is_rejected()
    {
        var json = CreateReleases(
            new TestRelease("nightly", "abcdef1234567890", [new TestAsset("api_abcdef2_x64.zip", OfficialDownloadUrl)]));

        var result = await ResolveAsync("abcdef1", json);

        Assert.False(result.Succeeded);
        Assert.Contains("asset version must start with commit ref", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("owner//repo@main", "main")]
    [InlineData("bad owner/repo@main", "main")]
    [InlineData("../repo@main", "main")]
    [InlineData("owner/repo@", "")]
    [InlineData("owner/repo", "")]
    public async Task Resolve_rejects_invalid_github_specs(string spec, string reference)
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            throw new InvalidOperationException("The GitHub API must not be requested for an invalid spec.")));
        var provider = new GitHubReleaseProvider(client);

        var result = await provider.ResolveAsync(
            new ReleaseSource("github", spec, reference),
            "api",
            "x64",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("format", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_tag_requires_exact_tag_and_exact_asset_version_without_warning()
    {
        var json = CreateReleases(
            new TestRelease("stable", "main", [new TestAsset("api_stable_x64.zip", OfficialDownloadUrl)]));

        var result = await ResolveAsync("stable", json);

        Assert.True(result.Succeeded);
        Assert.Equal("stable", result.Asset!.Tag);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Resolve_tag_asset_version_mismatch_succeeds_with_warning()
    {
        var json = CreateReleases(
            new TestRelease("stable", "main", [new TestAsset("api_1.2.0_x64.zip", OfficialDownloadUrl)]));

        var result = await ResolveAsync("stable", json);

        Assert.True(result.Succeeded);
        Assert.Equal("1.2.0", result.Asset!.Version);
        Assert.Contains("does not exactly match", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_semver_asset_version_mismatch_is_rejected()
    {
        var json = CreateReleases(
            new TestRelease("v1.2.3", "main", [new TestAsset("api_1.2.4_x64.zip", OfficialDownloadUrl)]));

        var result = await ResolveAsync("1.2.3", json);

        Assert.False(result.Succeeded);
        Assert.Contains("asset version must equal", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_selects_asset_for_requested_service_and_architecture()
    {
        var json = CreateReleases(
            new TestRelease(
                "v1.2.3",
                "main",
                [
                    new TestAsset("worker_v1.2.3_x64.zip", "https://example.com/worker.zip"),
                    new TestAsset("api_v1.2.3_arm64.zip", "https://example.com/arm64.zip"),
                    new TestAsset("api_v1.2.3_x64.zip", OfficialDownloadUrl)
                ]));

        var result = await ResolveAsync("v1.2.3", json, serviceId: "api", arch: "x64");

        Assert.True(result.Succeeded);
        Assert.Equal("api_v1.2.3_x64.zip", result.Asset!.AssetName);
        Assert.Equal(OfficialDownloadUrl, result.Asset.DownloadUrl);
    }

    [Fact]
    public async Task Resolve_reports_unmatched_refs_and_lists_assets_when_asset_is_missing()
    {
        var noRelease = await ResolveAsync(
            "missing-tag",
            CreateReleases(new TestRelease("other-tag", "main", [])));
        Assert.False(noRelease.Succeeded);
        Assert.Contains("missing-tag", noRelease.Error, StringComparison.Ordinal);
        Assert.Contains("owner/repo", noRelease.Error, StringComparison.Ordinal);

        var noAsset = await ResolveAsync(
            "v1.2.3",
            CreateReleases(new TestRelease(
                "v1.2.3",
                "main",
                [new TestAsset("worker_v1.2.3_x64.zip", "https://example.com/worker.zip")])));
        Assert.False(noAsset.Succeeded);
        Assert.Contains("worker_v1.2.3_x64.zip", noAsset.Error, StringComparison.Ordinal);
    }

    private static async Task<ReleaseProviderResult> ResolveAsync(
        string reference,
        string releasesJson,
        string serviceId = "api",
        string arch = "x64")
    {
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal("https://api.github.com/repos/owner/repo/releases?per_page=100", request.RequestUri!.ToString());
            Assert.Contains(request.Headers.UserAgent, value => value.Product?.Name == "nekostick-svchost");
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/vnd.github+json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releasesJson, Encoding.UTF8, "application/json")
            });
        }));
        var provider = new GitHubReleaseProvider(client);
        var source = new ReleaseSource("github", $"owner/repo@{reference}", reference);
        return await provider.ResolveAsync(source, serviceId, arch, CancellationToken.None);
    }

    private static string CreateReleases(params TestRelease[] releases) =>
        JsonSerializer.Serialize(releases.Select(release => new
        {
            draft = release.Draft,
            tag_name = release.Tag,
            target_commitish = release.TargetCommitish,
            prerelease = true,
            assets = release.Assets.Select(asset => new
            {
                name = asset.Name,
                browser_download_url = asset.DownloadUrl,
                digest = asset.Digest
            })
        }));

    private sealed record TestRelease(string Tag, string TargetCommitish, TestAsset[] Assets, bool Draft = false);

    private sealed record TestAsset(string Name, string? DownloadUrl, string? Digest = null);

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
