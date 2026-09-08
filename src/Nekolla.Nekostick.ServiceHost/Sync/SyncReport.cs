using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.ServiceHost.Sync;

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

    /// <summary>Gets the settings version consumed at the start of reconciliation, when available.</summary>
    public long? ConsumedSettingsVersion { get; init; }

    /// <summary>Gets a domain-specific reconciliation error, when applicable.</summary>
    public SyncErrorCode? FailureCode { get; init; }

    /// <summary>Gets additive notes such as orphan services swept from the global snapshot.</summary>
    public ImmutableArray<string> Notes { get; init; } = ImmutableArray<string>.Empty;
}