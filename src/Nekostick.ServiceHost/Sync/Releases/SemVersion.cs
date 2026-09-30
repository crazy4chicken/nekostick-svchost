using System.Globalization;
using System.Numerics;

namespace Nekostick.ServiceHost.Sync.Releases;

/// <summary>Represents a SemVer 2.0.0 version.</summary>
internal readonly struct SemVersion : IEquatable<SemVersion>
{
    private SemVersion(BigInteger major, BigInteger minor, BigInteger patch, string? prerelease, string? build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
    }

    /// <summary>Gets the major version number.</summary>
    public BigInteger Major { get; }

    /// <summary>Gets the minor version number.</summary>
    public BigInteger Minor { get; }

    /// <summary>Gets the patch version number.</summary>
    public BigInteger Patch { get; }

    /// <summary>Gets the prerelease identifiers, if present.</summary>
    public string? Prerelease { get; }

    /// <summary>Gets the build metadata, if present.</summary>
    public string? Build { get; }

    /// <summary>Attempts to parse a SemVer 2.0.0 version, optionally prefixed with lowercase v.</summary>
    public static bool TryParse(string? value, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        ReadOnlySpan<char> input = value.AsSpan();
        if (input[0] == 'v')
        {
            input = input[1..];
            if (input.IsEmpty)
            {
                return false;
            }
        }

        string? build = null;
        var buildIndex = input.IndexOf('+');
        if (buildIndex >= 0)
        {
            var buildSpan = input[(buildIndex + 1)..];
            if (!IsIdentifierList(buildSpan, prerelease: false))
            {
                return false;
            }

            build = new string(buildSpan);
            input = input[..buildIndex];
        }

        string? prerelease = null;
        var prereleaseIndex = input.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            var prereleaseSpan = input[(prereleaseIndex + 1)..];
            if (!IsIdentifierList(prereleaseSpan, prerelease: true))
            {
                return false;
            }

            prerelease = new string(prereleaseSpan);
            input = input[..prereleaseIndex];
        }

        var firstDot = input.IndexOf('.');
        if (firstDot <= 0)
        {
            return false;
        }

        var secondPart = input[(firstDot + 1)..];
        var secondDotOffset = secondPart.IndexOf('.');
        if (secondDotOffset <= 0)
        {
            return false;
        }

        var secondDot = firstDot + 1 + secondDotOffset;
        var majorText = input[..firstDot];
        var minorText = input[(firstDot + 1)..secondDot];
        var patchText = input[(secondDot + 1)..];
        if (patchText.IsEmpty || patchText.IndexOf('.') >= 0 ||
            !IsCoreNumber(majorText) || !IsCoreNumber(minorText) || !IsCoreNumber(patchText) ||
            !BigInteger.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !BigInteger.TryParse(minorText, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !BigInteger.TryParse(patchText, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch, prerelease, build);
        return true;
    }

    /// <summary>Compares core and prerelease values; build metadata is excluded by SemVer identity.</summary>
    public bool Equals(SemVersion other) =>
        Major == other.Major &&
        Minor == other.Minor &&
        Patch == other.Patch &&
        string.Equals(Prerelease, other.Prerelease, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        Major,
        Minor,
        Patch,
        Prerelease is null ? 0 : StringComparer.Ordinal.GetHashCode(Prerelease));

    /// <summary>Compares two versions using SemVer identity.</summary>
    public static bool operator ==(SemVersion left, SemVersion right) => left.Equals(right);

    /// <summary>Compares two versions using SemVer identity.</summary>
    public static bool operator !=(SemVersion left, SemVersion right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
    {
        var result = string.Concat(
            Major.ToString(CultureInfo.InvariantCulture), ".",
            Minor.ToString(CultureInfo.InvariantCulture), ".",
            Patch.ToString(CultureInfo.InvariantCulture));
        if (Prerelease is not null)
        {
            result = string.Concat(result, "-", Prerelease);
        }

        return Build is null ? result : string.Concat(result, "+", Build);
    }

    private static bool IsCoreNumber(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || (value.Length > 1 && value[0] == '0'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierList(ReadOnlySpan<char> value, bool prerelease)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        var identifierStart = 0;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && value[index] != '.')
            {
                var character = value[index];
                if (character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'Z') and
                    not (>= 'a' and <= 'z') and
                    not '-')
                {
                    return false;
                }

                continue;
            }

            var identifier = value[identifierStart..index];
            if (identifier.IsEmpty ||
                (prerelease && identifier.Length > 1 && identifier[0] == '0' && IsNumericIdentifier(identifier)))
            {
                return false;
            }

            identifierStart = index + 1;
        }

        return true;
    }

    private static bool IsNumericIdentifier(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
