using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Nekostick.ServiceHost.Compose;

public sealed partial class ComposeFileParser
{
    private static readonly Regex NameRegex = new(
        "^[a-z0-9][a-z0-9-]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256Regex = new(
        "^[0-9a-fA-F]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DurationRegex = new(
        "^(?<value>[0-9]+(?:\\.[0-9]+)?)(?<unit>ms|s|m|h)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? ReadOptionalString(
        IReadOnlyDictionary<string, YamlNode> entries,
        string key,
        string path,
        ICollection<ComposeValidationError> errors)
    {
        if (!entries.TryGetValue(key, out var node))
        {
            return null;
        }

        var value = Scalar(node);
        if (value is null)
        {
            errors.Add(new ComposeValidationError(
                $"{path}.{key}",
                "The value must be a scalar string.",
                Line(node)));
        }

        return value;
    }

    private static bool? ReadOptionalBool(
        IReadOnlyDictionary<string, YamlNode> entries,
        string key,
        string path,
        ICollection<ComposeValidationError> errors)
    {
        var value = ReadOptionalString(entries, key, path, errors);
        if (value is null)
        {
            return null;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        errors.Add(new ComposeValidationError(
            $"{path}.{key}",
            "The value must be true or false.",
            Line(entries[key])));
        return null;
    }

    private static TimeSpan? ParseDuration(
        string value,
        string path,
        IReadOnlyDictionary<string, YamlNode> entries,
        ICollection<ComposeValidationError> errors)
    {
        var match = DurationRegex.Match(value);
        if (!match.Success ||
            !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            errors.Add(new ComposeValidationError(path, "Timeout must use a positive duration such as 250ms or 5s.",
                entries.TryGetValue("timeout", out var node) ? Line(node) : null));
            return null;
        }

        var multiplier = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.TicksPerMillisecond,
            "s" => TimeSpan.TicksPerSecond,
            "m" => TimeSpan.TicksPerMinute,
            "h" => TimeSpan.TicksPerHour,
            _ => 0
        };
        var ticks = number * multiplier;
        if (ticks <= 0 || ticks > TimeSpan.MaxValue.Ticks || double.IsNaN(ticks) || double.IsInfinity(ticks))
        {
            errors.Add(new ComposeValidationError(path, "Timeout must be positive and finite.",
                entries.TryGetValue("timeout", out var node) ? Line(node) : null));
            return null;
        }

        return TimeSpan.FromTicks((long)ticks);
    }

    private static Dictionary<string, YamlNode> Entries(
        YamlMappingNode mapping,
        string path,
        ICollection<ComposeValidationError> errors)
    {
        var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var child in mapping.Children)
        {
            var key = Scalar(child.Key);
            if (key is null)
            {
                errors.Add(new ComposeValidationError(path, "Mapping keys must be scalar strings.", Line(child.Key)));
                continue;
            }

            if (!result.TryAdd(key, child.Value))
            {
                errors.Add(new ComposeValidationError($"{path}.{key}", "Duplicate field.", Line(child.Key)));
            }
        }

        return result;
    }

    private static void RejectUnknown(
        IReadOnlyDictionary<string, YamlNode> entries,
        string path,
        IEnumerable<string> allowed,
        ICollection<ComposeValidationError> errors)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var key in entries.Keys)
        {
            if (!allowedSet.Contains(key))
            {
                errors.Add(new ComposeValidationError($"{path}.{key}", "Unknown field."));
            }
        }
    }

    private static YamlMappingNode? RequireMapping(
        YamlNode node,
        string path,
        ICollection<ComposeValidationError> errors)
    {
        if (node is YamlMappingNode mapping)
        {
            return mapping;
        }

        errors.Add(new ComposeValidationError(path, "The value must be a YAML mapping.", Line(node)));
        return null;
    }

    private static string? Scalar(YamlNode node) =>
        node is YamlScalarNode scalar && scalar.Value is not null ? scalar.Value : null;

    private static int? Line(YamlNode? node) => node is null ? null : checked((int)node.Start.Line + 1);

    private static ComposeHealthCheckType InvalidHealthType(
        string value,
        string path,
        IReadOnlyDictionary<string, YamlNode> entries,
        ICollection<ComposeValidationError> errors)
    {
        errors.Add(new ComposeValidationError(
            $"{path}.type",
            $"Unknown health type '{value}'. Expected process, tcp, or http.",
            entries.TryGetValue("type", out var node) ? Line(node) : null));
        return ComposeHealthCheckType.Process;
    }

    private static ComposeStartMode InvalidStart(
        string value,
        string path,
        IReadOnlyDictionary<string, YamlNode> entries,
        ICollection<ComposeValidationError> errors)
    {
        errors.Add(new ComposeValidationError(
            $"{path}.start",
            $"Unknown start mode '{value}'. Expected eager or lazy.",
            entries.TryGetValue("start", out var node) ? Line(node) : null));
        return ComposeStartMode.Eager;
    }

    private static ComposeRestartPolicy InvalidRestart(
        string value,
        string path,
        IReadOnlyDictionary<string, YamlNode> entries,
        ICollection<ComposeValidationError> errors)
    {
        errors.Add(new ComposeValidationError(
            $"{path}.restart",
            $"Unknown restart policy '{value}'. Expected never, on-failure, or always.",
            entries.TryGetValue("restart", out var node) ? Line(node) : null));
        return ComposeRestartPolicy.OnFailure;
    }
}
