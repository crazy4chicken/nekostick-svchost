namespace Nekolla.Nekostick.ServiceHost.Sync;

/// <summary>Identifies a reconciliation failure that is not a Host configuration error.</summary>
public enum SyncErrorCode
{
    /// <summary>The source could not be downloaded, copied, or verified.</summary>
    SourceFailed,

    /// <summary>The desired state could not be reconciled with the Host.</summary>
    ReconcileFailed,

    /// <summary>The Host replacement succeeded but its source lock was not persisted.</summary>
    LockPersistFailed
}
