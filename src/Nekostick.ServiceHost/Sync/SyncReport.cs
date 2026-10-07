using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.Sync;

/// <summary>Aggregates one serialized reconciliation run.</summary>
public sealed record SyncReport(
    bool Succeeded,
    bool DataDirectoryAvailable,
    DateTimeOffset CompletedAt,
    ImmutableArray<ServiceSyncReport> Services,
    ConfigurationErrorCode? ErrorCode,
    string? Error)
{
    /// <summary>Gets whether all service reports succeeded.</summary>
    public bool HasFailures => !Succeeded || Services.Any(service => !service.Succeeded);

    /// <summary>Gets the settings version written by this reconciliation when an atomic Host replacement changed its settings row.</summary>
    public long? CommittedSettingsVersion { get; init; }

    /// <summary>Gets the settings version consumed by this reconciliation.</summary>
    internal long? ConsumedSettingsVersion { get; init; }

    /// <summary>Gets the Host configuration version committed by this run's final ReplaceAsync, or null when no configuration write occurred.</summary>
    public long? WrittenConfigurationVersion { get; init; }
    /// <summary>Gets a domain-specific reconciliation error, when applicable.</summary>
    public SyncErrorCode? FailureCode { get; init; }

    /// <summary>Gets additive notes such as orphan services swept from the global snapshot.</summary>
    public ImmutableArray<string> Notes { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Gets the source that triggered this reconciliation.</summary>
    public string Trigger { get; init; } = "unknown";
}