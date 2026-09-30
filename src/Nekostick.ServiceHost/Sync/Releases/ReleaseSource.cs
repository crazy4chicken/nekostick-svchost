namespace Nekostick.ServiceHost.Sync.Releases;

/// <summary>Describes a provider-specific release source.</summary>
public sealed record ReleaseSource(string ProviderKey, string Spec, string Ref)
{
    /// <summary>Parses a compose release value into its provider, raw spec, and ref.</summary>
    public static bool TryParse(string? value, out ReleaseSource? source)
    {
        source = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        var providerKey = value[..separator];
        if (!IsProviderKey(providerKey))
        {
            return false;
        }

        var spec = value[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var refSeparator = spec.IndexOf('@');
        var reference = refSeparator < 0 ? string.Empty : spec[(refSeparator + 1)..];
        source = new ReleaseSource(providerKey, spec, reference);
        return true;
    }

    private static bool IsProviderKey(string value)
    {
        if (value.Length == 0 || value[0] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and
                not '-')
            {
                return false;
            }
        }

        return true;
    }
}
