namespace Nekostick.ServiceHost.Sync;

/// <summary>Identifies a reconciliation outcome outside Host configuration errors.</summary>
public enum SyncErrorCode
{
    /// <summary>The source could not be downloaded, copied, or verified.</summary>
    SourceFailed,

    /// <summary>The desired state could not be reconciled with the Host.</summary>
    ReconcileFailed,

    /// <summary>A service is disabled but Host removal has not committed yet.</summary>
    RemovalPending
}
