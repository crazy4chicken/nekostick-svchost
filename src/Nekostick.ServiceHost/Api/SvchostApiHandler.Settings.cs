using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Settings;

namespace Nekostick.ServiceHost.Api;

public sealed partial class SvchostApiHandler
{
    private static readonly IReadOnlyDictionary<string, ISettingsGroupDefinition> SettingsGroupRegistry =
        new Dictionary<string, ISettingsGroupDefinition>(StringComparer.Ordinal)
        {
            ["releaseProviders"] = new SettingsGroupDefinition<Dictionary<string, ReleaseProviderSettings>>(
                "releaseProviders",
                settings => settings.ReleaseProviders,
                () => new Dictionary<string, ReleaseProviderSettings>(StringComparer.Ordinal),
                (settings, releaseProviders) => settings.ReleaseProviders = releaseProviders,
                ValidateReleaseProviders)
        };

    private async ValueTask<ExtensionStreamingResponse> HandleSettingsAsync(
        string method,
        Stream bodyStream,
        CancellationToken cancellationToken)
    {
        var normalizedMethod = method.ToUpperInvariant();
        if (normalizedMethod == "GET")
        {
            var read = await _settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return ErrorForConfiguration(read.Errors);
            }

            return JsonResponse(200, SettingsPayload(read.Value?.Settings));
        }

        if (normalizedMethod == "PUT")
        {
            return await HandlePutSettingsAsync(bodyStream, cancellationToken).ConfigureAwait(false);
        }

        return Error(404, "not_found", "The API endpoint was not found.");
    }

    private async ValueTask<ExtensionStreamingResponse> HandlePutSettingsAsync(
        Stream bodyStream,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(bodyStream, cancellationToken).ConfigureAwait(false);
        if (body.TooLarge)
        {
            return Error(413, "request_too_large", "The request body exceeds the 1 MiB limit.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.Bytes);
        }
        catch (JsonException)
        {
            return Error(400, "validation", "The request body must be valid JSON.");
        }

        var updates = new List<SettingsGroupUpdate>();
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Error(400, "validation", "The request body must be a JSON object of settings groups.");
            }

            foreach (var group in document.RootElement.EnumerateObject())
            {
                if (!SettingsGroupRegistry.TryGetValue(group.Name, out var definition))
                {
                    return Error(400, "validation", $"Unknown settings group '{group.Name}'.");
                }

                if (!definition.TryParse(group.Value, out var value, out var error))
                {
                    return Error(400, "validation", error ?? $"The '{group.Name}' settings group is invalid.");
                }

                updates.Add(new SettingsGroupUpdate(definition, value!));
            }
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SvchostSettings? savedSettings = null;
            var write = await _settingsStore.UpdateSettingsAsync(
                    settings =>
                    {
                        foreach (var update in updates)
                        {
                            update.Definition.Apply(settings, update.Value);
                        }

                        savedSettings = settings;
                        return settings;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                return ErrorForConfiguration(write.Errors);
            }

            return JsonResponse(200, SettingsPayload(savedSettings));
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static object SettingsPayload(SvchostSettings? settings) =>
        SettingsGroupRegistry.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Read(settings),
            StringComparer.Ordinal);

    private static string? ValidateReleaseProviders(
        Dictionary<string, ReleaseProviderSettings> releaseProviders)
    {
        foreach (var (providerKey, provider) in releaseProviders)
        {
            if (provider is null)
            {
                return $"releaseProviders.{providerKey} must be an object.";
            }

            if (provider.Mirrors is null)
            {
                return $"releaseProviders.{providerKey}.mirrors must be an array.";
            }

            for (var index = 0; index < provider.Mirrors.Count; index++)
            {
                var mirror = provider.Mirrors[index];
                if (string.IsNullOrWhiteSpace(mirror) ||
                    !Uri.TryCreate(mirror, UriKind.Absolute, out var uri) ||
                    (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                     !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
                    string.IsNullOrEmpty(uri.Host))
                {
                    return $"releaseProviders.{providerKey}.mirrors[{index}] must be an absolute HTTP or HTTPS URL.";
                }
            }
        }

        return null;
    }

    private interface ISettingsGroupDefinition
    {
        object Read(SvchostSettings? settings);

        bool TryParse(JsonElement input, out object? value, out string? error);

        void Apply(SvchostSettings settings, object value);
    }

    private sealed class SettingsGroupDefinition<T>(
        string name,
        Func<SvchostSettings, T?> read,
        Func<T> createEmpty,
        Action<SvchostSettings, T> apply,
        Func<T, string?> validate) : ISettingsGroupDefinition
        where T : class
    {
        public object Read(SvchostSettings? settings) =>
            settings is null ? createEmpty() : read(settings) ?? createEmpty();

        public bool TryParse(JsonElement input, out object? value, out string? error)
        {
            value = null;
            if (input.ValueKind != JsonValueKind.Object)
            {
                error = $"The '{name}' settings group must be an object.";
                return false;
            }

            T? parsed;
            try
            {
                parsed = input.Deserialize<T>(JsonOptions);
            }
            catch (JsonException)
            {
                error = $"The '{name}' settings group is invalid.";
                return false;
            }

            if (parsed is null)
            {
                error = $"The '{name}' settings group must not be null.";
                return false;
            }

            error = validate(parsed);
            if (error is not null)
            {
                return false;
            }

            value = parsed;
            return true;
        }

        public void Apply(SvchostSettings settings, object value) => apply(settings, (T)value);
    }

    private sealed record SettingsGroupUpdate(ISettingsGroupDefinition Definition, object Value);
}
