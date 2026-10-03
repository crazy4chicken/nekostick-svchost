namespace Nekostick.ServiceHost.Sync;

/// <summary>Describes how a service was handled during reconciliation.</summary>
public enum ServiceDecision
{
    Reused,
    Updated,
    Preserved,
    Failed,
    Skipped
}
