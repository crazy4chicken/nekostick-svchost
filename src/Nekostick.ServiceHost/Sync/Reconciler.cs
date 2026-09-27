using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;

namespace Nekostick.ServiceHost.Sync;

/// <summary>Serializes source resolution and atomic host configuration replacement.</summary>
public sealed partial class Reconciler
{
    /// <summary>The maximum full-snapshot replacement conflict attempts.</summary>
    public const int MaxReplaceAttempts = 5;

    private const string Owner = SvchostSettingsSchema.ExtensionId;
    private readonly SettingsStore _settingsStore;
    private readonly ComposeFileParser _composeFileParser;
    private readonly SourceResolver _sourceResolver;
    private readonly IExtensionFullConfigurationApi _fullConfiguration;
    private readonly IExtensionSupervisorApi? _supervisor;
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _serializationGate = new(1, 1);

    /// <summary>Creates a reconciler for one Host bridge and extension data directory.</summary>
    public Reconciler(
        SettingsStore settingsStore,
        ComposeFileParser composeFileParser,
        SourceResolver sourceResolver,
        IExtensionFullConfigurationApi fullConfiguration,
        string? dataDirectory = null,
        IExtensionSupervisorApi? supervisor = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _composeFileParser = composeFileParser ?? throw new ArgumentNullException(nameof(composeFileParser));
        _sourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
        _fullConfiguration = fullConfiguration ?? throw new ArgumentNullException(nameof(fullConfiguration));
        _supervisor = supervisor;
        _dataDirectory = dataDirectory ?? string.Empty;
    }

    /// <summary>Runs one complete serialized reconciliation.</summary>
    public ValueTask<SyncReport> ReconcileAsync(CancellationToken cancellationToken = default) =>
        ReconcileAsync(Array.Empty<Guid>(), Array.Empty<Guid>(), cancellationToken);

    /// <summary>
    /// Runs reconciliation while also removing IDs from a configuration deleted by a caller.
    /// </summary>
    public async ValueTask<SyncReport> ReconcileAsync(
        IEnumerable<Guid> extraManagedServiceIds,
        IEnumerable<Guid> extraManagedRouteIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extraManagedServiceIds);
        ArgumentNullException.ThrowIfNull(extraManagedRouteIds);

        var extraServices = extraManagedServiceIds.ToHashSet();
        var extraRoutes = extraManagedRouteIds.ToHashSet();
        await _serializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReconcileCoreAsync(extraServices, extraRoutes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serializationGate.Release();
        }
    }
}
