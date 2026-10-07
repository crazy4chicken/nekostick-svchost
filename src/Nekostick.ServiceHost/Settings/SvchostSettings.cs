using System.Text.Json.Serialization;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Sync;

namespace Nekostick.ServiceHost.Settings;

/// <summary>Identifies the extension settings schema understood by this assembly.</summary>
public static class SvchostSettingsSchema
{
    /// <summary>The current raw-settings schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The stable extension identifier used by the Host contracts.</summary>
    public const string ExtensionId = "nekostick.svchost";

    /// <summary>Settings written by the pre-rename extension identifier and migrated at bootstrap.</summary>
    public const string LegacyExtensionId = "nekolla.nekostick.svchost";

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
/// <summary>Tracks a service that must remain managed until its Host removal commits.</summary>
public sealed class RetiringServiceSettings
{
    /// <summary>Creates an empty retirement entry for JSON deserialization.</summary>
    public RetiringServiceSettings()
    {
    }

    /// <summary>Creates a service retirement entry.</summary>
    public RetiringServiceSettings(
        Guid serviceId,
        IEnumerable<Guid> routeIds,
        string configName,
        string serviceName,
        string scope)
    {
        ServiceId = serviceId;
        RouteIds = routeIds.Distinct().ToList();
        ConfigName = configName;
        ServiceName = serviceName;
        Scope = scope;
    }

    /// <summary>Gets or sets the Host service ID awaiting removal.</summary>
    [JsonPropertyName("serviceId")]
    public Guid ServiceId { get; set; }

    /// <summary>Gets or sets the associated Host route IDs.</summary>
    [JsonPropertyName("routeIds")]
    public List<Guid> RouteIds { get; set; } = new();

    /// <summary>Gets or sets the originating config name.</summary>
    [JsonPropertyName("configName")]
    public string ConfigName { get; set; } = string.Empty;

    /// <summary>Gets or sets the originating service name.</summary>
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Gets or sets the originating service scope.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "global";
}


/// <summary>Contains asset-download mirror prefixes for one release provider.</summary>
public sealed class ReleaseProviderSettings
{
    /// <summary>Gets or sets mirror URL prefixes, tried in their configured order.</summary>
    [JsonPropertyName("mirrors")]
    public List<string>? Mirrors { get; set; } = new();
}

/// <summary>Contains settings for reconciliation observability.</summary>
public sealed class SvchostObservabilitySettings
{
    /// <summary>Gets or sets the minimum reconciliation log level.</summary>
    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "information";
}

/// <summary>Represents the raw JSON document persisted as extension settings.</summary>
public sealed class SvchostSettings
{
    private SvchostObservabilitySettings _observability = new();

    /// <summary>Creates an empty settings document.</summary>
    public SvchostSettings()
    {
    }

    /// <summary>Creates settings with the supplied values.</summary>
    public SvchostSettings(
        string? apiKey,
        SvchostRouteSettings routes,
        IDictionary<string, SvchostConfigSettings>? configs = null,
        IDictionary<string, ReleaseProviderSettings>? releaseProviders = null,
        IEnumerable<RetiringServiceSettings>? retiring = null)
    {
        ApiKey = apiKey;
        Routes = routes ?? throw new ArgumentNullException(nameof(routes));
        Configs = configs is null
            ? new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal)
            : new Dictionary<string, SvchostConfigSettings>(configs, StringComparer.Ordinal);
        ReleaseProviders = releaseProviders is null
            ? new Dictionary<string, ReleaseProviderSettings>(StringComparer.Ordinal)
            : new Dictionary<string, ReleaseProviderSettings>(releaseProviders, StringComparer.Ordinal);
        Retiring = retiring?.ToList() ?? new List<RetiringServiceSettings>();
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

    /// <summary>Gets or sets provider-specific release download settings.</summary>
    [JsonPropertyName("releaseProviders")]
    public Dictionary<string, ReleaseProviderSettings>? ReleaseProviders { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>Gets or sets durable service removals awaiting Host completion.</summary>
    [JsonPropertyName("retiring")]
    public List<RetiringServiceSettings> Retiring { get; set; } = new();

    /// <summary>Gets or sets reconciliation observability settings.</summary>
    [JsonPropertyName("observability")]
    public SvchostObservabilitySettings Observability
    {
        get => _observability;
        set => _observability = value ?? new();
    }

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
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
                pair.Key.Equals("global", StringComparison.OrdinalIgnoreCase))
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

        if (Retiring is null)
        {
            errors.Add("retiring is required.");
        }
        else
        {
            var retiringServiceIds = new HashSet<Guid>();
            foreach (var retirement in Retiring)
            {
                if (retirement is null)
                {
                    errors.Add("retiring entries must not be null.");
                    continue;
                }

                if (!IsUuidV7(retirement.ServiceId))
                {
                    errors.Add("retiring.serviceId must be a UUID v7.");
                }
                else if (!retiringServiceIds.Add(retirement.ServiceId))
                {
                    errors.Add("retiring.serviceId values must be unique.");
                }

                if (!IsValidConfigName(retirement.ConfigName))
                {
                    errors.Add("retiring.configName has an invalid name.");
                }

                if (!IsValidName(retirement.ServiceName))
                {
                    errors.Add("retiring.serviceName has an invalid name.");
                }

                if (retirement.Scope is not ("global" or "document"))
                {
                    errors.Add("retiring.scope must be global or document.");
                }

                if (retirement.RouteIds is null)
                {
                    errors.Add("retiring.routeIds is required.");
                }
                else
                {
                    var routeIds = new HashSet<Guid>();
                    foreach (var routeId in retirement.RouteIds)
                    {
                        if (!IsUuidV7(routeId))
                        {
                            errors.Add("retiring.routeIds must contain UUID v7 values.");
                        }
                        else if (!routeIds.Add(routeId))
                        {
                            errors.Add("retiring.routeIds must not contain duplicates.");
                        }
                    }
                }
            }
        }

        return errors;
    }

    internal static void AddRetirement(
        IList<RetiringServiceSettings> retirements,
        RetiringServiceSettings retirement)
    {
        ArgumentNullException.ThrowIfNull(retirements);
        ArgumentNullException.ThrowIfNull(retirement);
        var existing = retirements.FirstOrDefault(entry => entry.ServiceId == retirement.ServiceId);
        if (existing is null)
        {
            retirements.Add(retirement);
            return;
        }

        existing.RouteIds = (existing.RouteIds ?? new List<Guid>())
            .Concat(retirement.RouteIds ?? new List<Guid>())
            .Distinct()
            .ToList();
    }

    internal static bool IsUuidV7(Guid value)
    {
        if (value == Guid.Empty)
        {
            return false;
        }

        var bytes = value.ToByteArray();
        return (bytes[7] & 0xF0) == 0x70;
    }

    private static bool IsValidConfigName(string? name) =>
        IsValidName(name) && !name!.Equals("global", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidName(string? name) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            name ?? string.Empty,
            SvchostSettingsSchema.NamePattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

/// <summary>Contains the successful settings read together with its untouched raw JSON.</summary>
public sealed record SettingsDocumentSnapshot(
    SvchostSettings? Settings,
    string? RawJson,
    long Version,
    ExtensionSettingsConfiguration? Configuration);
