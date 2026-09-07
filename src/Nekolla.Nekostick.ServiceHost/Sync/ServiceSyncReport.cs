using System.Collections.Immutable;

namespace Nekolla.Nekostick.ServiceHost.Sync;

/// <summary>Reports one service's source and configuration reconciliation result.</summary>
public sealed record ServiceSyncReport(
    string ConfigName,
    string ServiceName,
    bool Succeeded,
    bool Changed,
    Guid? ServiceId,
    ImmutableArray<Guid> RouteIds,
    string? Error,
    SyncErrorCode? FailureCode = null);
