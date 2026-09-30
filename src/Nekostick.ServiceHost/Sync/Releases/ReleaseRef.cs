namespace Nekostick.ServiceHost.Sync.Releases;

/// <summary>Identifies the interpretation applied to a release ref.</summary>
internal enum ReleaseRefKind
{
    /// <summary>The ref is a semantic version.</summary>
    SemVer,

    /// <summary>The ref is a hexadecimal commit prefix.</summary>
    CommitHash,

    /// <summary>The ref is an exact release tag.</summary>
    Tag
}

/// <summary>Classifies a release ref as SemVer, a commit hash, or an exact tag.</summary>
internal sealed record ReleaseRef(ReleaseRefKind Kind, string Value, SemVersion? Version)
{
    /// <summary>Classifies a ref, preferring commit hashes before semantic versions.</summary>
    public static ReleaseRef Classify(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (IsCommitHash(value))
        {
            return new ReleaseRef(ReleaseRefKind.CommitHash, value, null);
        }

        return SemVersion.TryParse(value, out var version)
            ? new ReleaseRef(ReleaseRefKind.SemVer, value, version)
            : new ReleaseRef(ReleaseRefKind.Tag, value, null);
    }

    private static bool IsCommitHash(string value)
    {
        if (value.Length is < 7 or > 40)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
