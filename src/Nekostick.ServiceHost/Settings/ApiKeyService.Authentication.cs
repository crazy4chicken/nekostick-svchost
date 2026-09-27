using System.Security.Cryptography;
using System.Text;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.Settings;

public sealed partial class ApiKeyService
{
    /// <summary>Verifies a presented key against the currently active key in constant time.</summary>
    public ValueTask<bool> VerifyAsync(
        string? presentedKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? activeKey;
        lock (_stateGate)
        {
            activeKey = _permanentKey ?? _bootstrapKey;
        }

        if (activeKey is null || presentedKey is null)
        {
            return ValueTask.FromResult(false);
        }

        var expectedBytes = Encoding.UTF8.GetBytes(activeKey);
        var presentedBytes = Encoding.UTF8.GetBytes(presentedKey);
        return ValueTask.FromResult(CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes));
    }

    /// <summary>Persists a permanent key and atomically exits bootstrap mode.</summary>
    public async ValueTask<ConfigurationWriteResult> SetPermanentKeyAsync(
        string newKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newKey) || newKey.Length < MinimumKeyLength)
        {
            return ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation));
        }

        lock (_stateGate)
        {
            if (_readonly)
            {
                return ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported));
            }
        }

        var result = await _settingsStore.UpdateSettingsAsync(
                settings =>
                {
                    settings.ApiKey = newKey;
                    return settings;
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            if (HasError(result.Errors, ConfigurationErrorCode.Unsupported))
            {
                SetReadonly(Settings);
            }

            return result;
        }

        lock (_stateGate)
        {
            _permanentKey = newKey;
            _bootstrapKey = null;
            _bootstrap = false;
        }

        var latest = await _settingsStore.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (latest.IsSuccess)
        {
            lock (_stateGate)
            {
                _settings = latest.Value!.Settings;
            }
        }

        return result;
    }
}
