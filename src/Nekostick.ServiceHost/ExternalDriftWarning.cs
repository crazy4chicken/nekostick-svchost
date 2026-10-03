using Nekostick.ServiceHost.Sync;

namespace Nekostick.ServiceHost;

internal static class ExternalDriftWarning
{
    public static bool IsEligibleTrigger(string trigger) =>
        string.Equals(trigger, "startup", StringComparison.Ordinal) ||
        string.Equals(trigger, "drift-host-version", StringComparison.Ordinal);

    public static bool IsDriftCorrection(string trigger, ServiceSyncReport service) =>
        IsEligibleTrigger(trigger) &&
        service.Decision == ServiceDecision.Updated &&
        !service.Diffs.IsDefaultOrEmpty;
}
