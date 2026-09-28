using System.Text;

namespace Nekostick.ServiceHost.Compose;

/// <summary>Identifies the parsed form of one host launch template.</summary>
public enum ComposeTemplateExpressionKind
{
    /// <summary>A service-local environment variable or a dynamic launch value.</summary>
    Name,

    /// <summary>A host-provided environment variable passthrough.</summary>
    Host,

    /// <summary>An environment variable read from another service.</summary>
    Service
}

/// <summary>Describes one well-formed, unescaped host launch template.</summary>
public readonly record struct ComposeTemplateExpression(
    int Start,
    int Length,
    ComposeTemplateExpressionKind Kind,
    string Name,
    string? Target)
{
    /// <summary>Gets the exclusive end offset of the expression.</summary>
    public int End => checked(Start + Length);
}

/// <summary>Scans and rewrites host launch templates in compose values.</summary>
public static class ComposeTemplate
{
    /// <summary>
    /// Enumerates well-formed, unescaped <c>${...}</c> expressions in a value.
    /// Malformed or unterminated expressions are ignored so that the Host can
    /// report its normal validation error.
    /// </summary>
    public static IEnumerable<ComposeTemplateExpression> Enumerate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EnumerateCore(value);
    }

    /// <summary>Invokes a callback for each well-formed, unescaped expression.</summary>
    public static void Visit(string value, Action<ComposeTemplateExpression> visitor)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(visitor);
        foreach (var expression in EnumerateCore(value))
        {
            visitor(expression);
        }
    }

    /// <summary>
    /// Rewrites service-name suffixes to their Host service IDs. Guid suffixes and
    /// all non-service expressions are copied byte-for-byte.
    /// </summary>
    /// <exception cref="ComposeTemplateResolutionException">
    /// Thrown when a non-Guid service suffix cannot be resolved.
    /// </exception>
    public static string Rewrite(string value, Func<string, Guid?> resolveTarget)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(resolveTarget);

        StringBuilder? rewritten = null;
        var sourceOffset = 0;
        foreach (var expression in EnumerateCore(value))
        {
            if (expression.Kind != ComposeTemplateExpressionKind.Service ||
                Guid.TryParse(expression.Target, out _))
            {
                continue;
            }

            var target = expression.Target!;
            var serviceId = resolveTarget(target);
            if (serviceId is null)
            {
                throw new ComposeTemplateResolutionException(target);
            }

            rewritten ??= new StringBuilder(value.Length);
            rewritten.Append(value, sourceOffset, expression.Start - sourceOffset);
            rewritten.Append("${");
            rewritten.Append(expression.Name);
            rewritten.Append('@');
            rewritten.Append(serviceId.Value.ToString("D"));
            rewritten.Append('}');
            sourceOffset = expression.End;
        }

        if (rewritten is null)
        {
            return value;
        }

        rewritten.Append(value, sourceOffset, value.Length - sourceOffset);
        return rewritten.ToString();
    }

    private static IEnumerable<ComposeTemplateExpression> EnumerateCore(string value)
    {
        for (var index = 0; index < value.Length - 2; index++)
        {
            if (value[index] != '$' || value[index + 1] != '{' || IsEscaped(value, index))
            {
                continue;
            }

            var close = value.IndexOf('}', index + 2);
            if (close < 0)
            {
                yield break;
            }

            var bodyStart = index + 2;
            var bodyLength = close - bodyStart;
            if (TryParse(value, bodyStart, bodyLength, out var kind, out var name, out var target))
            {
                yield return new ComposeTemplateExpression(
                    index,
                    close - index + 1,
                    kind,
                    name,
                    target);
            }

            index = close;
        }
    }

    private static bool TryParse(
        string value,
        int bodyStart,
        int bodyLength,
        out ComposeTemplateExpressionKind kind,
        out string name,
        out string? target)
    {
        kind = default;
        name = string.Empty;
        target = null;

        if (bodyLength >= 5 &&
            value[bodyStart] == 'H' &&
            value[bodyStart + 1] == 'O' &&
            value[bodyStart + 2] == 'S' &&
            value[bodyStart + 3] == 'T' &&
            value[bodyStart + 4] == ':')
        {
            var hostNameStart = bodyStart + 5;
            var hostNameLength = bodyLength - 5;
            if (!IsName(value, hostNameStart, hostNameLength))
            {
                return false;
            }

            kind = ComposeTemplateExpressionKind.Host;
            name = value.Substring(hostNameStart, hostNameLength);
            return true;
        }

        var at = value.IndexOf('@', bodyStart, bodyLength);
        if (at >= 0)
        {
            var bodyEnd = bodyStart + bodyLength;
            if (value.IndexOf('@', at + 1, bodyEnd - at - 1) >= 0 ||
                !IsName(value, bodyStart, at - bodyStart) ||
                at + 1 >= bodyEnd)
            {
                return false;
            }

            kind = ComposeTemplateExpressionKind.Service;
            name = value.Substring(bodyStart, at - bodyStart);
            target = value.Substring(at + 1, bodyEnd - at - 1);
            return true;
        }

        if (!IsName(value, bodyStart, bodyLength))
        {
            return false;
        }

        kind = ComposeTemplateExpressionKind.Name;
        name = value.Substring(bodyStart, bodyLength);
        return true;
    }

    private static bool IsName(string value, int start, int length)
    {
        if (length == 0 || !(IsLetter(value[start]) || value[start] == '_'))
        {
            return false;
        }

        for (var index = start + 1; index < start + length; index++)
        {
            if (!IsLetter(value[index]) && !IsDigit(value[index]) && value[index] != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsDigit(char value) => value is >= '0' and <= '9';

    private static bool IsEscaped(string value, int dollarIndex)
    {
        var slashCount = 0;
        for (var index = dollarIndex - 1; index >= 0 && value[index] == '\\'; index--)
        {
            slashCount++;
        }

        return (slashCount & 1) != 0;
    }
}

/// <summary>Indicates that a compose service-name template target could not be resolved.</summary>
public sealed class ComposeTemplateResolutionException : InvalidOperationException
{
    /// <summary>Creates an exception for an unresolved service-name suffix.</summary>
    public ComposeTemplateResolutionException(string target)
        : base($"The compose template target '{target}' could not be resolved to a service ID.")
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    /// <summary>Gets the unresolved target name.</summary>
    public string Target { get; }
}
