namespace Nekostick.ServiceHost.Logs;

/// <summary>Identifies the log destination for one managed service.</summary>
public sealed record ServiceLogTarget(Guid ServiceId, string ServiceName, string LogDirectory);
