using System.Collections.Immutable;
using YamlDotNet.RepresentationModel;

namespace Nekolla.Nekostick.ServiceHost.Compose;

public sealed partial class ComposeFileParser
{
    private static ComposeSource? ParseSource(
        YamlNode node,
        string path,
        ICollection<ComposeValidationError> errors)
    {
        var mapping = RequireMapping(node, path, errors);
        if (mapping is null)
        {
            return null;
        }

        var entries = Entries(mapping, path, errors);
        RejectUnknown(entries, path, new[] { "url", "path", "sha256" }, errors);

        var url = ReadOptionalString(entries, "url", path, errors);
        var localPath = ReadOptionalString(entries, "path", path, errors);
        var sha256 = ReadOptionalString(entries, "sha256", path, errors);
        var hasUrl = !string.IsNullOrWhiteSpace(url);
        var hasPath = !string.IsNullOrWhiteSpace(localPath);
        if (hasUrl == hasPath)
        {
            errors.Add(new ComposeValidationError(
                path,
                "Exactly one of url or path must be supplied.",
                Line(mapping)));
            return null;
        }

        if (hasUrl)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ComposeValidationError(
                    $"{path}.url",
                    "Source URLs must be absolute HTTPS URLs.",
                    Line(entries["url"])));
            }
        }

        if (sha256 is not null && !Sha256Regex.IsMatch(sha256))
        {
            errors.Add(new ComposeValidationError(
                $"{path}.sha256",
                "sha256 must contain exactly 64 hexadecimal characters.",
                Line(entries["sha256"])));
        }

        return new ComposeSource(url, localPath, sha256?.ToLowerInvariant());
    }

    private static ComposeHealthCheck ParseHealth(
        IReadOnlyDictionary<string, YamlNode> serviceEntries,
        string servicePath,
        ICollection<ComposeValidationError> errors)
    {
        if (!serviceEntries.TryGetValue("health", out var healthNode))
        {
            return ComposeHealthCheck.ProcessDefault;
        }

        var path = $"{servicePath}.health";
        var mapping = RequireMapping(healthNode, path, errors);
        if (mapping is null)
        {
            return ComposeHealthCheck.ProcessDefault;
        }

        var entries = Entries(mapping, path, errors);
        RejectUnknown(entries, path, new[] { "type", "path", "timeout" }, errors);
        var typeText = ReadOptionalString(entries, "type", path, errors) ?? "process";
        var type = typeText.ToLowerInvariant() switch
        {
            "process" => ComposeHealthCheckType.Process,
            "tcp" => ComposeHealthCheckType.Tcp,
            "http" => ComposeHealthCheckType.Http,
            _ => InvalidHealthType(typeText, path, entries, errors)
        };
        var httpPath = ReadOptionalString(entries, "path", path, errors);
        if (type == ComposeHealthCheckType.Http && string.IsNullOrWhiteSpace(httpPath))
        {
            errors.Add(new ComposeValidationError(
                $"{path}.path",
                "HTTP health checks require path.",
                Line(mapping)));
        }
        else if (type == ComposeHealthCheckType.Http && !httpPath!.StartsWith("/", StringComparison.Ordinal))
        {
            errors.Add(new ComposeValidationError(
                $"{path}.path",
                "HTTP health paths must start with '/'.",
                Line(entries["path"])));
        }

        var timeoutText = ReadOptionalString(entries, "timeout", path, errors) ?? "5s";
        var timeout = ParseDuration(timeoutText, $"{path}.timeout", entries, errors) ?? TimeSpan.FromSeconds(5);
        try
        {
            return new ComposeHealthCheck(type, httpPath, timeout);
        }
        catch (ArgumentException exception)
        {
            errors.Add(new ComposeValidationError(path, exception.Message, Line(healthNode)));
            return ComposeHealthCheck.ProcessDefault;
        }
    }

    private static ComposeRoute? ParseRoute(
        IReadOnlyDictionary<string, YamlNode> serviceEntries,
        string servicePath,
        ICollection<ComposeValidationError> errors)
    {
        if (!serviceEntries.TryGetValue("route", out var routeNode))
        {
            return null;
        }

        var path = $"{servicePath}.route";
        var mapping = RequireMapping(routeNode, path, errors);
        if (mapping is null)
        {
            return null;
        }

        var entries = Entries(mapping, path, errors);
        RejectUnknown(entries, path, new[] { "prefix", "strip", "methods", "hosts" }, errors);
        var prefix = ReadOptionalString(entries, "prefix", path, errors);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            errors.Add(new ComposeValidationError(
                $"{path}.prefix",
                "Route prefix is required.",
                Line(mapping)));
            return null;
        }

        if (!prefix.StartsWith("/", StringComparison.Ordinal))
        {
            errors.Add(new ComposeValidationError(
                $"{path}.prefix",
                "Route prefix must start with '/'.",
                Line(entries["prefix"])));
        }

        var strip = ReadOptionalBool(entries, "strip", path, errors) ?? false;
        var methods = ParseStringSequence(entries, "methods", path, errors) ?? ImmutableArray<string>.Empty;
        var hosts = ParseStringSequence(entries, "hosts", path, errors) ?? ImmutableArray<string>.Empty;
        return new ComposeRoute(prefix, strip, methods, hosts);
    }

    private static ComposeStartMode ParseStart(
        IReadOnlyDictionary<string, YamlNode> entries,
        string path,
        ICollection<ComposeValidationError> errors)
    {
        var value = ReadOptionalString(entries, "start", path, errors) ?? "eager";
        return value.ToLowerInvariant() switch
        {
            "eager" => ComposeStartMode.Eager,
            "lazy" => ComposeStartMode.Lazy,
            _ => InvalidStart(value, path, entries, errors)
        };
    }

    private static ComposeRestartPolicy ParseRestart(
        IReadOnlyDictionary<string, YamlNode> entries,
        string path,
        ICollection<ComposeValidationError> errors)
    {
        var value = ReadOptionalString(entries, "restart", path, errors) ?? "on-failure";
        return value.ToLowerInvariant() switch
        {
            "never" => ComposeRestartPolicy.Never,
            "on-failure" => ComposeRestartPolicy.OnFailure,
            "always" => ComposeRestartPolicy.Always,
            _ => InvalidRestart(value, path, entries, errors)
        };
    }

    private static ImmutableDictionary<string, string>? ParseEnvironment(
        IReadOnlyDictionary<string, YamlNode> entries,
        string servicePath,
        ICollection<ComposeValidationError> errors)
    {
        if (!entries.TryGetValue("env", out var envNode))
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var path = $"{servicePath}.env";
        var mapping = RequireMapping(envNode, path, errors);
        if (mapping is null)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Entries(mapping, path, errors))
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                errors.Add(new ComposeValidationError(path, "Environment variable names must not be empty.", Line(pair.Value)));
                continue;
            }

            var value = Scalar(pair.Value);
            if (value is null)
            {
                errors.Add(new ComposeValidationError(
                    $"{path}.{pair.Key}",
                    "Environment values must be scalar strings.",
                    Line(pair.Value)));
                continue;
            }

            result[pair.Key] = value;
        }

        return result.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private static ImmutableArray<string>? ParseStringSequence(
        IReadOnlyDictionary<string, YamlNode> entries,
        string key,
        string path,
        ICollection<ComposeValidationError> errors)
    {
        if (!entries.TryGetValue(key, out var node))
        {
            return null;
        }

        if (node is not YamlSequenceNode sequence)
        {
            errors.Add(new ComposeValidationError(
                $"{path}.{key}",
                "The value must be a YAML sequence of strings.",
                Line(node)));
            return ImmutableArray<string>.Empty;
        }

        var values = ImmutableArray.CreateBuilder<string>(sequence.Children.Count);
        foreach (var child in sequence.Children)
        {
            var value = Scalar(child);
            if (value is null)
            {
                errors.Add(new ComposeValidationError(
                    $"{path}.{key}",
                    "The value must be a YAML sequence of strings.",
                    Line(child)));
            }
            else
            {
                values.Add(value);
            }
        }

        return values.ToImmutable();
    }
}
