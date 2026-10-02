using System.Collections.Immutable;

namespace Nekostick.ServiceHost.Sync;

/// <summary>Reports one service's source and configuration reconciliation result.</summary>
public sealed record ServiceSyncReport(
    string ConfigName,
    string ServiceName,
    bool Succeeded,
    bool Changed,
    Guid? ServiceId,
    ImmutableArray<Guid> RouteIds,
    string? Error,
    SyncErrorCode? FailureCode = null)
{
    /// <summary>
    /// Gets non-fatal warnings associated with this service. This additive field is
    /// included in sync report payloads without changing existing fields.
    /// </summary>
    public ImmutableArray<string> Warnings { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Gets whether a failed service remains in global configuration because its
    /// existing deployment is retained locally. Source failures continue to use
    /// the existing <c>source</c> error kind; this additive field distinguishes
    /// node-local degradation from a global removal.
    /// </summary>
    public bool NodeLocal { get; init; }

    /// <summary>
    /// Gets the directory where this service's output logs are stored. This additive
    /// field is included in sync report payloads without changing existing fields.
    /// </summary>
    public string? LogDirectory { get; init; }
}
