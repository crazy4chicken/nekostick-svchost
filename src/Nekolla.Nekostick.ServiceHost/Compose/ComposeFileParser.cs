using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nekolla.Nekostick.ServiceHost.Compose;

/// <summary>Parses and validates the deliberately small svchost YAML dialect.</summary>
public sealed partial class ComposeFileParser
{

    /// <summary>Parses one YAML document, throwing a typed validation exception on invalid input.</summary>
    public ComposeFile Parse(string yaml)
    {
        if (yaml is null)
        {
            throw new ArgumentNullException(nameof(yaml));
        }

        var errors = new List<ComposeValidationError>();
        YamlDocument document;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1)
            {
                errors.Add(new ComposeValidationError("document", "Exactly one YAML document is required."));
                throw new ComposeValidationException(errors);
            }

            document = stream.Documents[0];
        }
        catch (ComposeValidationException)
        {
            throw;
        }
        catch (YamlException exception)
        {
            errors.Add(new ComposeValidationError("document", exception.Message, checked((int)exception.Start.Line + 1)));
            throw new ComposeValidationException(errors);
        }

        var root = RequireMapping(document.RootNode, "document", errors);
        if (root is null)
        {
            throw new ComposeValidationException(errors);
        }

        var rootEntries = Entries(root, "document", errors);
        RejectUnknown(rootEntries, "document", new[] { "services", "strictSources" }, errors);
        var strictSources = ReadOptionalBool(rootEntries, "strictSources", "document", errors) ?? false;
        if (!rootEntries.TryGetValue("services", out var servicesNode))
        {
            errors.Add(new ComposeValidationError("services", "The services mapping is required.", Line(root)));
            throw new ComposeValidationException(errors);
        }

        var servicesMapping = RequireMapping(servicesNode, "services", errors);
        if (servicesMapping is null)
        {
            throw new ComposeValidationException(errors);
        }

        var services = new Dictionary<string, ComposeService>(StringComparer.Ordinal);
        foreach (var servicePair in Entries(servicesMapping, "services", errors))
        {
            var serviceName = servicePair.Key;
            if (!NameRegex.IsMatch(serviceName))
            {
                errors.Add(new ComposeValidationError(
                    $"services.{serviceName}",
                    "Service names must match ^[a-z0-9][a-z0-9-]{0,62}$.",
                    Line(servicePair.Value)));
                continue;
            }

            var service = ParseService(serviceName, servicePair.Value, strictSources, errors);
            if (service is not null)
            {
                services.Add(serviceName, service);
            }
        }

        if (errors.Count > 0)
        {
            throw new ComposeValidationException(errors);
        }

        return new ComposeFile(services, strictSources);
    }

    /// <summary>Attempts to parse one document without throwing validation exceptions.</summary>
    public bool TryParse(
        string yaml,
        out ComposeFile? composeFile,
        out ImmutableArray<ComposeValidationError> errors)
    {
        try
        {
            composeFile = Parse(yaml);
            errors = ImmutableArray<ComposeValidationError>.Empty;
            return true;
        }
        catch (ComposeValidationException exception)
        {
            composeFile = null;
            errors = exception.Errors;
            return false;
        }
    }

    private static ComposeService? ParseService(
        string serviceName,
        YamlNode node,
        bool strictSources,
        ICollection<ComposeValidationError> errors)
    {
        var path = $"services.{serviceName}";
        var mapping = RequireMapping(node, path, errors);
        if (mapping is null)
        {
            return null;
        }

        var entries = Entries(mapping, path, errors);
        RejectUnknown(entries, path, new[] { "source", "args", "env", "start", "restart", "health", "route" }, errors);

        if (!entries.TryGetValue("source", out var sourceNode))
        {
            errors.Add(new ComposeValidationError($"{path}.source", "The source mapping is required.", Line(mapping)));
            return null;
        }

        var warnings = new List<string>();
        var source = ParseSource(sourceNode, $"{path}.source", strictSources, errors, warnings);
        if (source is null)
        {
            return null;
        }

        var args = ParseStringSequence(entries, "args", path, errors) ?? ImmutableArray<string>.Empty;
        var environment = ParseEnvironment(entries, path, errors) ??
            ImmutableDictionary<string, string>.Empty;
        var start = ParseStart(entries, path, errors);
        var restart = ParseRestart(entries, path, errors);
        var health = ParseHealth(entries, path, errors);
        var route = ParseRoute(entries, path, errors);

        try
        {
            return new ComposeService(source, args, environment, start, restart, health, route, warnings);
        }
        catch (ArgumentException exception)
        {
            errors.Add(new ComposeValidationError(path, exception.Message, Line(node)));
            return null;
        }
    }

}
