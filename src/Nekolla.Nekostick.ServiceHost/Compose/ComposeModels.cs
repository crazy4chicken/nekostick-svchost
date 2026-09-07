using System.Collections.Immutable;

namespace Nekolla.Nekostick.ServiceHost.Compose;

/// <summary>Represents one validated svchost YAML document.</summary>
public sealed class ComposeFile
{
    /// <summary>Creates a compose document.</summary>
    public ComposeFile(IReadOnlyDictionary<string, ComposeService> services)
    {
        Services = services is null
            ? throw new ArgumentNullException(nameof(services))
            : services.ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>Gets services keyed by their validated names.</summary>
    public ImmutableDictionary<string, ComposeService> Services { get; }
}

/// <summary>Describes one service in a compose document.</summary>
public sealed class ComposeService
{
    /// <summary>Creates a service definition.</summary>
    public ComposeService(
        ComposeSource source,
        IEnumerable<string>? args = null,
        IReadOnlyDictionary<string, string>? environment = null,
        ComposeStartMode start = ComposeStartMode.Eager,
        ComposeRestartPolicy restart = ComposeRestartPolicy.OnFailure,
        ComposeHealthCheck? health = null,
        ComposeRoute? route = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Args = (args ?? Array.Empty<string>()).ToImmutableArray();
        Environment = environment is null
            ? ImmutableDictionary<string, string>.Empty
            : environment.ToImmutableDictionary(StringComparer.Ordinal);
        Start = start;
        Restart = restart;
        Health = health ?? ComposeHealthCheck.ProcessDefault;
        Route = route;
    }

    /// <summary>Gets the source declaration.</summary>
    public ComposeSource Source { get; }

    /// <summary>Gets process arguments.</summary>
    public ImmutableArray<string> Args { get; }

    /// <summary>Gets process environment overrides.</summary>
    public ImmutableDictionary<string, string> Environment { get; }

    /// <summary>Gets the start mode.</summary>
    public ComposeStartMode Start { get; }

    /// <summary>Gets the restart policy.</summary>
    public ComposeRestartPolicy Restart { get; }

    /// <summary>Gets the health-check declaration.</summary>
    public ComposeHealthCheck Health { get; }

    /// <summary>Gets the optional route declaration.</summary>
    public ComposeRoute? Route { get; }
}

/// <summary>Describes an online or local executable source.</summary>
public sealed class ComposeSource
{
    /// <summary>Creates a source declaration; exactly one URL or path must be supplied.</summary>
    public ComposeSource(string? url, string? path, string? sha256 = null)
    {
        Url = string.IsNullOrWhiteSpace(url) ? null : url;
        Path = string.IsNullOrWhiteSpace(path) ? null : path;
        Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256;
    }

    /// <summary>Gets the HTTPS source URL, if present.</summary>
    public string? Url { get; }

    /// <summary>Gets the local source path, if present.</summary>
    public string? Path { get; }

    /// <summary>Gets the optional expected SHA-256 digest.</summary>
    public string? Sha256 { get; }

    /// <summary>Gets whether this is an online source.</summary>
    public bool IsUrl => Url is not null;
}

/// <summary>Controls when a service is started.</summary>
public enum ComposeStartMode
{
    /// <summary>Start with the configuration.</summary>
    Eager,

    /// <summary>Start on first request.</summary>
    Lazy
}

/// <summary>Controls service restart behavior.</summary>
public enum ComposeRestartPolicy
{
    /// <summary>Never restart.</summary>
    Never,

    /// <summary>Restart after failure.</summary>
    OnFailure,

    /// <summary>Always restart.</summary>
    Always
}

/// <summary>Identifies the health-check mechanism.</summary>
public enum ComposeHealthCheckType
{
    /// <summary>Check process liveness.</summary>
    Process,

    /// <summary>Check a loopback TCP endpoint.</summary>
    Tcp,

    /// <summary>Check an HTTP endpoint.</summary>
    Http
}

/// <summary>Describes service health checking.</summary>
public sealed class ComposeHealthCheck
{
    /// <summary>The default process health check.</summary>
    public static ComposeHealthCheck ProcessDefault { get; } =
        new(ComposeHealthCheckType.Process, null, TimeSpan.FromSeconds(5));

    /// <summary>Creates health-check settings.</summary>
    public ComposeHealthCheck(ComposeHealthCheckType type, string? path, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (type == ComposeHealthCheckType.Http && string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("HTTP health checks require a path.", nameof(path));
        }

        Type = type;
        Path = path;
        Timeout = timeout;
    }

    /// <summary>Gets the health-check type.</summary>
    public ComposeHealthCheckType Type { get; }

    /// <summary>Gets the HTTP path when configured.</summary>
    public string? Path { get; }

    /// <summary>Gets the timeout.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>Describes an optional route declaration.</summary>
public sealed class ComposeRoute
{
    /// <summary>Creates route settings.</summary>
    public ComposeRoute(
        string prefix,
        bool strip = false,
        IEnumerable<string>? methods = null,
        IEnumerable<string>? hosts = null)
    {
        Prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
        Strip = strip;
        Methods = (methods ?? Array.Empty<string>()).ToImmutableArray();
        Hosts = (hosts ?? Array.Empty<string>()).ToImmutableArray();
    }

    /// <summary>Gets the path prefix.</summary>
    public string Prefix { get; }

    /// <summary>Gets whether the prefix is stripped before forwarding.</summary>
    public bool Strip { get; }

    /// <summary>Gets optional method constraints.</summary>
    public ImmutableArray<string> Methods { get; }

    /// <summary>Gets optional host constraints.</summary>
    public ImmutableArray<string> Hosts { get; }
}

/// <summary>Identifies one validation failure in a YAML document.</summary>
public sealed record ComposeValidationError(string Path, string Message, int? Line = null);

/// <summary>Reports one or more strict compose validation errors.</summary>
public sealed class ComposeValidationException : Exception
{
    /// <summary>Creates a compose validation exception.</summary>
    public ComposeValidationException(IEnumerable<ComposeValidationError> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors.ToImmutableArray();
    }

    /// <summary>Gets all validation failures.</summary>
    public ImmutableArray<ComposeValidationError> Errors { get; }

    private static string BuildMessage(IEnumerable<ComposeValidationError> errors) =>
        string.Join(
            Environment.NewLine,
            errors.Select(error => error.Line is null
                ? $"{error.Path}: {error.Message}"
                : $"{error.Path} (line {error.Line.Value}): {error.Message}"));
}
