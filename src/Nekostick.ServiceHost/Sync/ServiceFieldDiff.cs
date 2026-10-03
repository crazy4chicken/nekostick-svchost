namespace Nekostick.ServiceHost.Sync;

/// <summary>Describes one sanitized field change in a managed service configuration.</summary>
public readonly record struct ServiceFieldDiff(string Field, string? OldValue, string? NewValue);
