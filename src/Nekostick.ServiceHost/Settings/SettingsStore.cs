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
            // A fresh node has no settings row; host API >=1.4 reports NoSettings, older hosts
            // report NotFound. Treat both as an empty document so the caller can create and
            // persist the initial settings.
            return result.Errors.Any(error =>
                    error.Code == ConfigurationErrorCode.NoSettings ||
                    error.Code == ConfigurationErrorCode.NotFound)
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

    /// <summary>Serializes and writes a settings model with bounded conflict retries.</summary>
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
                new ConfigurationError(ConfigurationErrorCode.Validation));
        }

        string rawJson;
        try
        {
            rawJson = JsonSerializer.Serialize(settings, SerializerOptions);
        }
        catch (JsonException)
        {
            return ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation));
        }

        return await WriteRawSettingsAsync(expectedVersion, rawJson, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes validated raw JSON without normalizing its formatting or property order.</summary>
    public async ValueTask<ConfigurationWriteResult> WriteRawSettingsAsync(
        long expectedVersion,
        string rawJson,
        CancellationToken cancellationToken = default)
{
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (!TryValidateRawJson(rawJson, out _))
        {
            return ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation));
        }

        var version = expectedVersion;
        ConfigurationWriteResult? lastResult = null;
        for (var attempt = 0; attempt < MaxConflictAttempts; attempt++)
        {
            lastResult = await WriteRawOnceAsync(version, rawJson, cancellationToken).ConfigureAwait(false);
            if (lastResult.IsSuccess || !IsConflict(lastResult))
            {
                return lastResult;
            }

            var reread = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (!reread.IsSuccess)
            {
                return ConfigurationWriteResult.Failure(reread.Errors.ToArray());
            }

            version = reread.Value!.Version;
            // A conflict means another writer won; preserve that writer's exact JSON on retry.
            if (reread.Value.RawJson is not null)
            {
                rawJson = reread.Value.RawJson;
            }
        }

        return lastResult ?? ConfigurationWriteResult.Failure(
            new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict));
    }

    /// <summary>Reads, transforms, and writes settings with bounded conflict retries.</summary>
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

            var current = read.Value!.Settings ?? SvchostSettings.CreateInitial();
            SvchostSettings updated;
            try
            {
                updated = update(current) ?? throw new InvalidOperationException("The settings update returned null.");
            }
            catch (ArgumentException)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation));
            }
            catch (InvalidOperationException)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation));
            }

            var validationErrors = updated.Validate();
            if (validationErrors.Count > 0)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation));
            }

            string rawJson;
            try
            {
                rawJson = JsonSerializer.Serialize(updated, SerializerOptions);
            }
            catch (JsonException)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Validation));
            }

            lastResult = await WriteRawOnceAsync(read.Value.Version, rawJson, cancellationToken)
                .ConfigureAwait(false);
            if (lastResult.IsSuccess || !IsConflict(lastResult))
            {
                return lastResult;
            }
        }

        return lastResult ?? ConfigurationWriteResult.Failure(
            new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict));
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

    private static ConfigurationReadResult<T> ValidationFailure<T>(string message)
    {
        _ = message;
        return ConfigurationReadResult<T>.Failure(new ConfigurationError(ConfigurationErrorCode.Validation));
    }
}
