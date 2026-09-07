using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.ServiceHost.Sync;

/// <summary>Contains resolved desired state used by the pure full-snapshot diff.</summary>
public sealed record DesiredServiceState(
    string ConfigName,
    string ServiceName,
    ServiceConfiguration Service,
    RouteConfiguration? Route,
    LockServiceEntry Lock,
    bool SourceReused);
