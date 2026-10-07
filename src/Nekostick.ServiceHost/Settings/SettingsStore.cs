using System.Text.Json;
using System.Text.Json.Serialization;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.Settings;

/// <summary>Reads and writes the extension's raw JSON settings document.</summary>
public sealed class SettingsStore
{
    /// <summary>The maximum number of optimistic-conflict attempts per operation.</summary>
    public const int MaxConflictAttempts = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    private readonly IExtensionConfigurationApi _configurationApi;
    private readonly string _extensionId;

    /// <summary>Creates a settings store over the Host configuration API.</summary>
    public SettingsStore(
        IExtensionConfigurationApi configurationApi,
        string extensionId = SvchostSettingsSchema.ExtensionId)
    {
        _configurationApi = configurationApi ?? throw new ArgumentNullException(nameof(configurationApi));
        _extensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
    }

    /// <summary>Reads and validates settings while retaining the exact raw JSON string.</summary>
    public async ValueTask<ConfigurationReadResult<SettingsDocumentSnapshot>> ReadSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _configurationApi.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            // A fresh extension has no settings document; API 1.4 reports NoSettings.
            return result.Errors.Any(error => error.Code == ConfigurationErrorCode.NoSettings)
                ? ConfigurationReadResult<SettingsDocumentSnapshot>.Success(
                    new SettingsDocumentSnapshot(null, null, 0, null))
                : ConfigurationReadResult<SettingsDocumentSnapshot>.Failure(result.Errors.ToArray());
        }

        var configuration = result.Value;
        if (configuration is null)
        {
            return ConfigurationReadResult<SettingsDocumentSnapshot>.Success(
                new SettingsDocumentSnapshot(null, null, 0, null));
        }

        if (configuration.SchemaVersion != SvchostSettingsSchema.CurrentVersion ||
            string.IsNullOrWhiteSpace(configuration.SettingsJson))
        {
            return ValidationFailure<SettingsDocumentSnapshot>("The persisted settings schema is unsupported.");
        }

        try
        {
            using var document = JsonDocument.Parse(configuration.SettingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ValidationFailure<SettingsDocumentSnapshot>("The persisted settings document must be an object.");
            }

            var settings = JsonSerializer.Deserialize<SvchostSettings>(configuration.SettingsJson, SerializerOptions);
            if (settings is null)
            {
                return ValidationFailure<SettingsDocumentSnapshot>("The persisted settings document is empty.");
            }

            var validationErrors = settings.Validate();
            if (validationErrors.Count > 0)
            {
                return ValidationFailure<SettingsDocumentSnapshot>(string.Join(" ", validationErrors));
            }

            return ConfigurationReadResult<SettingsDocumentSnapshot>.Success(
                new SettingsDocumentSnapshot(
                    settings,
                    configuration.SettingsJson,
                    configuration.Version,
                    configuration));
        }
        catch (JsonException exception)
        {
            return ValidationFailure<SettingsDocumentSnapshot>(
                $"The persisted settings document is invalid JSON: {exception.Message}");
        }
    }

    /// <summary>Serializes and writes a validated settings model.</summary>
    public async ValueTask<ConfigurationWriteResult> WriteSettingsAsync(
        long expectedVersion,
        SvchostSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);

        var validationErrors = settings.Validate();
        if (validationErrors.Count > 0)
        {
            return ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation, string.Join(" ", validationErrors)));
        }

        string rawJson;
        try
        {
            rawJson = SerializeSettings(settings);
        }
        catch (JsonException exception)
        {
            return ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation, ExtensionErrorDetail.FromException(exception).Message));
        }

        return await WriteRawSettingsAsync(expectedVersion, rawJson, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serializes settings with this store's persisted JSON options.</summary>
    public string SerializeSettings(SvchostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, SerializerOptions);
    }

    /// <summary>Creates a full-replacement settings row at its observed entity version.</summary>
    public ExtensionSettingsConfiguration CreateExtensionSettingsConfiguration(
        SvchostSettings settings,
        long version)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        var validationErrors = settings.Validate();
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", validationErrors), nameof(settings));
        }

        return new ExtensionSettingsConfiguration(
            _extensionId,
            SvchostSettingsSchema.CurrentVersion,
            SerializeSettings(settings),
            version);
    }


    /// <summary>Writes validated raw JSON without normalizing its formatting or property order.</summary>
    public async ValueTask<ConfigurationWriteResult> WriteRawSettingsAsync(
        long expectedVersion,
        string rawJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (!TryValidateRawJson(rawJson, out var error))
        {
            return ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation, error!));
        }

        return await WriteRawOnceAsync(expectedVersion, rawJson, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads and transforms settings, writing only changed documents with bounded conflict retries.</summary>
    public async ValueTask<ConfigurationWriteResult> UpdateSettingsAsync(
        Func<SvchostSettings, SvchostSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        ConfigurationWriteResult? lastResult = null;
        for (var attempt = 0; attempt < MaxConflictAttempts; attempt++)
        {
            var read = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return ConfigurationWriteResult.Failure(read.Errors.ToArray());
            }

            var snapshot = read.Value!;
            var current = snapshot.Settings ?? SvchostSettings.CreateInitial();
            SvchostSettings updated;
            try
            {
                updated = update(current) ?? throw new InvalidOperationException("The settings update returned null.");
            }
            catch (ArgumentException exception)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation, ExtensionErrorDetail.FromException(exception).Message));
            }
            catch (InvalidOperationException exception)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation, ExtensionErrorDetail.FromException(exception).Message));
            }

            var validationErrors = updated.Validate();
            if (validationErrors.Count > 0)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation, string.Join(" ", validationErrors)));
            }

            string rawJson;
            try
            {
                rawJson = JsonSerializer.Serialize(updated, SerializerOptions);
            }
            catch (JsonException exception)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation, ExtensionErrorDetail.FromException(exception).Message));
            }

            if (snapshot.RawJson is { } existingJson && JsonDocumentsEqual(existingJson, rawJson))
            {
                // Confirm the row version before treating the update as a no-op.
                var confirmation = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (!confirmation.IsSuccess)
                {
                    return ConfigurationWriteResult.Failure(confirmation.Errors.ToArray());
                }

                var confirmed = confirmation.Value!;
                if (confirmed.Version != snapshot.Version ||
                    confirmed.RawJson is null ||
                    !JsonDocumentsEqual(existingJson, confirmed.RawJson))
                {
                    continue;
                }

                return ConfigurationWriteResult.Success(confirmed.Version);
            }

            lastResult = await WriteRawOnceAsync(snapshot.Version, rawJson, cancellationToken)
                .ConfigureAwait(false);
            if (lastResult.IsSuccess || !IsConflict(lastResult))
            {
                return lastResult;
            }
        }

        return lastResult ?? ConfigurationWriteResult.Failure(
            new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict, "The settings update exhausted its conflict attempts."));
    }

    private static bool JsonDocumentsEqual(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }

    private async ValueTask<ConfigurationWriteResult> WriteRawOnceAsync(
        long expectedVersion,
        string rawJson,
        CancellationToken cancellationToken)
    {
        var settings = new ExtensionSettingsConfiguration(
            _extensionId,
            SvchostSettingsSchema.CurrentVersion,
            rawJson,
            expectedVersion);
        return await _configurationApi.WriteSettingsAsync(expectedVersion, settings, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryValidateRawJson(string rawJson, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            error = "The settings JSON is required.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The settings document must be an object.";
                return false;
            }

            var settings = JsonSerializer.Deserialize<SvchostSettings>(rawJson, SerializerOptions);
            if (settings is null)
            {
                error = "The settings document is empty.";
                return false;
            }

            var validationErrors = settings.Validate();
            if (validationErrors.Count > 0)
            {
                error = string.Join(" ", validationErrors);
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool IsConflict(ConfigurationWriteResult result) =>
        !result.IsSuccess && result.Errors.Any(error => error.Code == ConfigurationErrorCode.ConcurrencyConflict);

    private static ConfigurationReadResult<T> ValidationFailure<T>(string message) =>
        ConfigurationReadResult<T>.Failure(new ConfigurationError(ConfigurationErrorCode.Validation, message));
}
