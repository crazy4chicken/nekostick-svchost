using System.Security.Cryptography;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.ServiceHost.Settings;

/// <summary>Describes the result of startup settings probing and bootstrap selection.</summary>
public sealed record ApiKeyInitializationResult(
    bool Succeeded,
    bool Readonly,
    bool Bootstrap,
    SvchostSettings? Settings,
    ConfigurationErrorCode? ErrorCode);

/// <summary>Owns the per-start bootstrap key and the persisted permanent key.</summary>
public sealed partial class ApiKeyService
{
    /// <summary>The minimum accepted permanent API key length.</summary>
    public const int MinimumKeyLength = 16;

    /// <summary>The maximum number of startup probe attempts.</summary>
    public const int MaxProbeAttempts = 3;

    private readonly SettingsStore _settingsStore;
    private readonly IExtensionHostBridge13 _bridge;
    private readonly object _stateGate = new();
    private string? _permanentKey;
    private string? _bootstrapKey;
    private SvchostSettings? _settings;
    private bool _readonly;
    private bool _bootstrap;

    /// <summary>Creates the API key state machine over a settings store and API 1.3 bridge.</summary>
    public ApiKeyService(SettingsStore settingsStore, IExtensionHostBridge13 bridge)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <summary>Gets whether startup probing found a read-only configuration store.</summary>
    public bool IsReadonly
    {
        get
        {
            lock (_stateGate)
            {
                return _readonly;
            }
        }
    }

    /// <summary>Gets whether the one-time bootstrap key is currently active.</summary>
    public bool IsBootstrap
    {
        get
        {
            lock (_stateGate)
            {
                return _bootstrap;
            }
        }
    }

    /// <summary>Gets the latest successfully read settings model.</summary>
    public SvchostSettings? Settings
    {
        get
        {
            lock (_stateGate)
            {
                return _settings;
            }
        }
    }

    private void ResetState()
    {
        lock (_stateGate)
        {
            _permanentKey = null;
            _bootstrapKey = null;
            _settings = null;
            _readonly = false;
            _bootstrap = false;
        }
    }

    private void Activate(SvchostSettings settings)
    {
        lock (_stateGate)
        {
            _settings = settings;
            _readonly = false;
            _permanentKey = string.IsNullOrEmpty(settings.ApiKey) ? null : settings.ApiKey;
            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                var bytes = RandomNumberGenerator.GetBytes(32);
                _bootstrapKey = Convert.ToBase64String(bytes)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
                _bootstrap = true;
            }
            else
            {
                _bootstrapKey = null;
                _bootstrap = false;
            }
        }

        string? bootstrapKey;
        lock (_stateGate)
        {
            bootstrapKey = _bootstrapKey;
        }

        if (bootstrapKey is not null)
        {
            _bridge.LogWriter.WriteText(
                ExtensionLogLevel.Warning,
                $"nekostick.svchost bootstrap api key: {bootstrapKey}");
        }
    }

    private void SetReadonly(SvchostSettings? settings)
    {
        lock (_stateGate)
        {
            _settings = settings;
            _readonly = true;
            _bootstrap = false;
            _bootstrapKey = null;
            _permanentKey = string.IsNullOrEmpty(settings?.ApiKey) ? null : settings.ApiKey;
        }

        _bridge.Logger.Report(ExtensionLogLevel.Warning, "settings-readonly");
    }

    private void ReportDegraded(string code) =>
        _bridge.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, code));

    private static ApiKeyInitializationResult FailureResult(
        IReadOnlyCollection<ConfigurationError> errors)
    {
        var code = errors.FirstOrDefault()?.Code ?? ConfigurationErrorCode.StorageUnavailable;
        return new ApiKeyInitializationResult(false, false, false, null, code);
    }

    private static bool HasError(
        IEnumerable<ConfigurationError> errors,
        ConfigurationErrorCode code) => errors.Any(error => error.Code == code);

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), cancellationToken);
}
