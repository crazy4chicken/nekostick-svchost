using System.Text.Json.Serialization;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.ServiceHost.Sync;

namespace Nekolla.Nekostick.ServiceHost.Settings;

/// <summary>Identifies the extension settings schema understood by this assembly.</summary>
public static class SvchostSettingsSchema
{
    /// <summary>The current raw-settings schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The stable extension identifier used by the Host contracts.</summary>
    public const string ExtensionId = "nekolla.nekostick.svchost";

    /// <summary>The configuration and data-directory name validation expression.</summary>
    public const string NamePattern = "^[a-z0-9][a-z0-9-]{0,62}$";
}

/// <summary>Contains the two stable routes owned by the extension.</summary>
public sealed class SvchostRouteSettings
{
    /// <summary>Creates route IDs for a new settings document.</summary>
    public SvchostRouteSettings()
        : this(Guid.CreateVersion7(), Guid.CreateVersion7())
    {
    }

    /// <summary>Creates route settings with explicit stable IDs.</summary>
    public SvchostRouteSettings(Guid api, Guid webui)
    {
        Api = api;
        Webui = webui;
    }

    /// <summary>Gets or sets the management API route ID.</summary>
    [JsonPropertyName("api")]
    public Guid Api { get; set; }

    /// <summary>Gets or sets the WebUI route ID.</summary>
    [JsonPropertyName("webui")]
    public Guid Webui { get; set; }
}

/// <summary>Contains one named YAML document and its reproducibility lock.</summary>
public sealed class SvchostConfigSettings
{
    /// <summary>Creates an empty configuration entry.</summary>
    public SvchostConfigSettings()
    {
    }

    /// <summary>Creates a configuration entry.</summary>
    public SvchostConfigSettings(
        string yaml,
        LockModel? @lock = null,
        IEnumerable<string>? stopped = null)
    {
        Yaml = yaml ?? throw new ArgumentNullException(nameof(yaml));
        Lock = @lock ?? new LockModel();
        Stopped = stopped?.ToArray() ?? Array.Empty<string>();
    }

    /// <summary>Gets or sets the original YAML text.</summary>
    [JsonPropertyName("yaml")]
    public string Yaml { get; set; } = string.Empty;

    /// <summary>Gets or sets the source lock for this configuration.</summary>
    [JsonPropertyName("lock")]
    public LockModel Lock { get; set; } = new();
 
    /// <summary>Gets or sets service names that should remain disabled.</summary>
    [JsonPropertyName("stopped")]
    public string[] Stopped { get; set; } = Array.Empty<string>();
}

/// <summary>Represents the raw JSON document persisted as extension settings.</summary>
public sealed class SvchostSettings
{
    /// <summary>Creates an empty settings document.</summary>
    public SvchostSettings()
    {
    }

    /// <summary>Creates settings with the supplied values.</summary>
    public SvchostSettings(
        string? apiKey,
        SvchostRouteSettings routes,
        IDictionary<string, SvchostConfigSettings>? configs = null)
    {
        ApiKey = apiKey;
        Routes = routes ?? throw new ArgumentNullException(nameof(routes));
        Configs = configs is null
            ? new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal)
            : new Dictionary<string, SvchostConfigSettings>(configs, StringComparer.Ordinal);
    }

    /// <summary>Gets or sets the permanent API key, if configured.</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets the stable extension-owned route IDs.</summary>
    [JsonPropertyName("routes")]
    public SvchostRouteSettings Routes { get; set; } = new();

    /// <summary>Gets or sets named YAML configurations.</summary>
    [JsonPropertyName("configs")]
    public Dictionary<string, SvchostConfigSettings> Configs { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>Creates the first settings document for a host.</summary>
    public static SvchostSettings CreateInitial() => new(null, new SvchostRouteSettings());

    /// <summary>Validates this settings document without touching the Host.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Routes is null)
        {
            errors.Add("routes is required.");
        }
        else
        {
            if (!IsUuidV7(Routes.Api))
            {
                errors.Add("routes.api must be a UUID v7.");
            }

            if (!IsUuidV7(Routes.Webui))
            {
                errors.Add("routes.webui must be a UUID v7.");
            }
        }

        if (Configs is null)
        {
            errors.Add("configs is required.");
            return errors;
        }

        foreach (var pair in Configs)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    pair.Key,
                    SvchostSettingsSchema.NamePattern,
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                errors.Add($"configs.{pair.Key} has an invalid name.");
            }

            if (pair.Value is null)
            {
                errors.Add($"configs.{pair.Key} must not be null.");
                continue;
            }

            if (pair.Value.Yaml is null)
            {
                errors.Add($"configs.{pair.Key}.yaml must not be null.");
            }

            if (pair.Value.Lock is null)
            {
                errors.Add($"configs.{pair.Key}.lock must not be null.");
            }
 
            if (pair.Value.Stopped is null)
            {
                errors.Add($"configs.{pair.Key}.stopped must not be null.");
            }
            else
            {
                foreach (var stoppedName in pair.Value.Stopped)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(
                            stoppedName ?? string.Empty,
                            SvchostSettingsSchema.NamePattern,
                            System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    {
                        errors.Add($"configs.{pair.Key}.stopped contains an invalid service name.");
                    }
                }
            }
        }

        return errors;
    }

    private static bool IsUuidV7(Guid value)
    {
        if (value == Guid.Empty)
        {
            return false;
        }

        var bytes = value.ToByteArray();
        return (bytes[7] & 0xF0) == 0x70;
    }
}

/// <summary>Contains the successful settings read together with its untouched raw JSON.</summary>
public sealed record SettingsDocumentSnapshot(
    SvchostSettings? Settings,
    string? RawJson,
    long Version,
    ExtensionSettingsConfiguration? Configuration);
