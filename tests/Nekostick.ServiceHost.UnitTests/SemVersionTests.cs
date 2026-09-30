using System.Numerics;
using Nekostick.ServiceHost.Sync.Releases;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class SemVersionTests
{
    [Fact]
    public void TryParse_accepts_v_prefix_prerelease_and_build_metadata()
    {
        var parsed = SemVersion.TryParse("v1.2.3-alpha.1+build.5", out var version);

        Assert.True(parsed);
        Assert.Equal(new BigInteger(1), version.Major);
        Assert.Equal(new BigInteger(2), version.Minor);
        Assert.Equal(new BigInteger(3), version.Patch);
        Assert.Equal("alpha.1", version.Prerelease);
        Assert.Equal("build.5", version.Build);
    }

    [Fact]
    public void Equality_ignores_build_metadata_but_preserves_prerelease_identity()
    {
        Assert.True(SemVersion.TryParse("1.2.3-rc.1+build.1", out var first));
        Assert.True(SemVersion.TryParse("v1.2.3-rc.1+build.2", out var second));
        Assert.True(SemVersion.TryParse("1.2.3-rc.2+build.1", out var differentPrerelease));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, differentPrerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-pre..one")]
    [InlineData("1.2.3-pre?fix")]
    [InlineData(" 1.2.3")]
    public void TryParse_rejects_values_outside_semver_grammar(string value)
    {
        Assert.False(SemVersion.TryParse(value, out _));
    }

    [Fact]
    public void Release_ref_classification_distinguishes_semver_hashes_and_tags()
    {
        Assert.Equal(ReleaseRefKind.SemVer, ReleaseRef.Classify("v1.2.3").Kind);
        Assert.Equal(ReleaseRefKind.CommitHash, ReleaseRef.Classify("aBcDeF1").Kind);
        Assert.Equal(ReleaseRefKind.CommitHash, ReleaseRef.Classify(new string('f', 40)).Kind);
        Assert.Equal(ReleaseRefKind.Tag, ReleaseRef.Classify("stable").Kind);
        Assert.Equal(ReleaseRefKind.Tag, ReleaseRef.Classify("abcdef").Kind);
    }
}
