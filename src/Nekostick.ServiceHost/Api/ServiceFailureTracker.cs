using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.Api;

internal sealed class ServiceFailureTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ServiceFailureSnapshot> _observations = new();

    public void Observe(Guid serviceId, ExtensionServiceLifecycleState state)
    {
        lock (_gate)
        {
            _observations.TryGetValue(serviceId, out var previous);
            _observations[serviceId] = new ServiceFailureSnapshot(
                state == ExtensionServiceLifecycleState.Failed
                    ? previous.ConsecutiveFailedObservations + 1
                    : 0);
        }
    }

    public void Reset(Guid serviceId)
    {
        lock (_gate)
        {
            _observations.Remove(serviceId);
        }
    }

    public ServiceFailureSnapshot GetSnapshot(Guid serviceId)
    {
        lock (_gate)
        {
            return _observations.TryGetValue(serviceId, out var observation)
                ? observation
                : default;
        }
    }
}

internal readonly record struct ServiceFailureSnapshot(int ConsecutiveFailedObservations);
