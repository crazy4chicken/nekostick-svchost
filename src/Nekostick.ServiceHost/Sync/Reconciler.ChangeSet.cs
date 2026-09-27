using System.Collections.Immutable;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.ServiceHost.Sync;

public sealed partial class Reconciler
{
    /// <summary>
    /// Builds the atomic full-snapshot replacement while preserving every unmanaged value verbatim.
    /// </summary>
    public ConfigurationChangeSet BuildChangeSet(
        HostConfigurationSnapshot snapshot,
        IEnumerable<Guid> managedServiceIds,
        IEnumerable<Guid> managedRouteIds,
        IEnumerable<ServiceConfiguration> desiredServices,
        IEnumerable<RouteConfiguration> desiredRoutes,
        IEnumerable<Guid>? preservedServiceIds = null,
        IEnumerable<Guid>? preservedRouteIds = null,
        IEnumerable<Guid>? configuredLockServiceIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(managedServiceIds);
        ArgumentNullException.ThrowIfNull(managedRouteIds);
        ArgumentNullException.ThrowIfNull(desiredServices);
        ArgumentNullException.ThrowIfNull(desiredRoutes);

        var managedServices = managedServiceIds.ToHashSet();
        var desiredServiceArray = desiredServices.ToImmutableArray();
        var desiredServiceIds = desiredServiceArray.Select(service => service.Id).ToHashSet();
        foreach (var desired in desiredServiceArray)
        {
            managedServices.Add(desired.Id);
        }

        var preservedServices = preservedServiceIds?.ToHashSet() ?? new HashSet<Guid>();
        var lockServiceIds = configuredLockServiceIds?.ToHashSet() ?? managedServices;
        var orphanSweep = FindOrphanSweep(snapshot, lockServiceIds, desiredServiceIds);
        var services = snapshot.Services
            .Where(service =>
                (!managedServices.Contains(service.Id) || preservedServices.Contains(service.Id)) &&
                !orphanSweep.ServiceIds.Contains(service.Id))
            .Concat(desiredServiceArray)
            .ToImmutableArray();

        var managedRoutes = managedRouteIds.ToHashSet();
        var desiredRouteArray = desiredRoutes.ToImmutableArray();
        foreach (var desired in desiredRouteArray)
        {
            managedRoutes.Add(desired.Id);
        }

        var preservedRoutes = preservedRouteIds?.ToHashSet() ?? new HashSet<Guid>();
        foreach (var route in snapshot.Routes)
        {
            if (route.Target is MicroserviceRouteTargetConfiguration microservice &&
                preservedServices.Contains(microservice.ServiceId) &&
                IsOwnedRoute(route.MetadataJson))
            {
                preservedRoutes.Add(route.Id);
            }
        }

        var routes = snapshot.Routes
            .Where(route =>
                preservedRoutes.Contains(route.Id) ||
                (!managedRoutes.Contains(route.Id) && !IsOwnedRoute(route.MetadataJson)))
            .Concat(desiredRouteArray)
            .ToImmutableArray();

        foreach (var note in orphanSweep.Notes)
        {
            System.Diagnostics.Debug.WriteLine($"{Owner}: {note}");
        }

        return new ConfigurationChangeSet(
            snapshot.GlobalSettings,
            routes,
            services,
            snapshot.ExtensionRecords,
            snapshot.ExtensionSettings);
    }

    private static ServiceConfiguration PreserveServiceVersion(
        ServiceConfiguration desired,
        HostConfigurationSnapshot snapshot,
        bool forceUpdate)
    {
        var existing = snapshot.Services.FirstOrDefault(service => service.Id == desired.Id);
        if (existing is null)
        {
            return desired;
        }

        var preserved = new ServiceConfiguration(
            desired.Id,
            desired.Enabled,
            desired.FileName,
            desired.ArgumentList,
            desired.WorkingDirectory,
            desired.Environment,
            desired.StartMode,
            desired.RestartPolicy,
            desired.HealthCheck,
            existing.CreatedAt,
            DateTimeOffset.UtcNow,
            existing.Version);
        return !forceUpdate && SemanticallyEqualIgnoringVersion(preserved, existing) ? existing : preserved;
    }

    private static RouteConfiguration PreserveRouteVersion(
        RouteConfiguration desired,
        HostConfigurationSnapshot snapshot)
    {
        var existing = snapshot.Routes.FirstOrDefault(route => route.Id == desired.Id);
        if (existing is null)
        {
            return desired;
        }

        var preserved = new RouteConfiguration(
            desired.Id,
            desired.Enabled,
            desired.Matcher,
            desired.Target,
            desired.Priority,
            desired.Forwarding,
            desired.RequestHeaderRewrites,
            desired.ResponseHeaderRewrites,
            desired.MetadataJson,
            existing.CreatedAt,
            DateTimeOffset.UtcNow,
            existing.Version,
            desired.ClientIpRatePolicy,
            desired.MaxRequestBodyBytes,
            desired.MaxRequestHeaderBytes,
            desired.MaxConcurrentRequests,
            desired.RequestReadTimeout,
            desired.ProxyRetries);
        return SemanticallyEqualIgnoringVersion(preserved, existing) ? existing : preserved;
    }

    private static bool SemanticallyEqualIgnoringVersion(
        ImmutableArray<ServiceConfiguration> left,
        ImmutableArray<ServiceConfiguration> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var rightById = new Dictionary<Guid, ServiceConfiguration>();
        foreach (var service in right)
        {
            if (!rightById.TryAdd(service.Id, service))
            {
                return false;
            }
        }

        return left.All(service =>
            rightById.TryGetValue(service.Id, out var other) &&
            SemanticallyEqualIgnoringVersion(service, other));
    }

    private static bool SemanticallyEqualIgnoringVersion(
        ImmutableArray<RouteConfiguration> left,
        ImmutableArray<RouteConfiguration> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var rightById = new Dictionary<Guid, RouteConfiguration>();
        foreach (var route in right)
        {
            if (!rightById.TryAdd(route.Id, route))
            {
                return false;
            }
        }

        return left.All(route =>
            rightById.TryGetValue(route.Id, out var other) &&
            SemanticallyEqualIgnoringVersion(route, other));
    }

    private static bool SemanticallyEqualIgnoringVersion(
        ServiceConfiguration left,
        ServiceConfiguration right) =>
        left.Id == right.Id &&
        left.Enabled == right.Enabled &&
        string.Equals(left.FileName, right.FileName, StringComparison.Ordinal) &&
        left.ArgumentList.SequenceEqual(right.ArgumentList) &&
        string.Equals(left.WorkingDirectory, right.WorkingDirectory, StringComparison.Ordinal) &&
        EnvironmentEquals(left.Environment, right.Environment) &&
        left.StartMode == right.StartMode &&
        left.RestartPolicy == right.RestartPolicy &&
        left.HealthCheck.Type == right.HealthCheck.Type &&
        string.Equals(left.HealthCheck.HttpPath, right.HealthCheck.HttpPath, StringComparison.Ordinal) &&
        left.HealthCheck.Timeout == right.HealthCheck.Timeout &&
        left.CreatedAt == right.CreatedAt;

    private static bool SemanticallyEqualIgnoringVersion(
        RouteConfiguration left,
        RouteConfiguration right) =>
        left.Id == right.Id &&
        left.Enabled == right.Enabled &&
        MatchersEqual(left.Matcher, right.Matcher) &&
        TargetsEqual(left.Target, right.Target) &&
        left.Priority == right.Priority &&
        ForwardingEqual(left.Forwarding, right.Forwarding) &&
        HeaderRewritesEqual(left.RequestHeaderRewrites, right.RequestHeaderRewrites) &&
        HeaderRewritesEqual(left.ResponseHeaderRewrites, right.ResponseHeaderRewrites) &&
        string.Equals(left.MetadataJson, right.MetadataJson, StringComparison.Ordinal) &&
        left.CreatedAt == right.CreatedAt &&
        Equals(left.ClientIpRatePolicy, right.ClientIpRatePolicy) &&
        left.MaxRequestBodyBytes == right.MaxRequestBodyBytes &&
        left.MaxRequestHeaderBytes == right.MaxRequestHeaderBytes &&
        left.MaxConcurrentRequests == right.MaxConcurrentRequests &&
        left.RequestReadTimeout == right.RequestReadTimeout &&
        Equals(left.ProxyRetries, right.ProxyRetries);

    private static bool EnvironmentEquals(
        ImmutableDictionary<string, string> left,
        ImmutableDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        return left.All(pair =>
            right.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static bool MatchersEqual(
        RouteMatcherConfiguration left,
        RouteMatcherConfiguration right) =>
        left.Type == right.Type &&
        string.Equals(left.Pattern, right.Pattern, StringComparison.Ordinal) &&
        left.HostPatterns.SequenceEqual(right.HostPatterns) &&
        left.Methods.SequenceEqual(right.Methods);

    private static bool TargetsEqual(
        RouteTargetConfiguration left,
        RouteTargetConfiguration right)
    {
        if (left is MicroserviceRouteTargetConfiguration leftMicroservice &&
            right is MicroserviceRouteTargetConfiguration rightMicroservice)
        {
            return leftMicroservice.ServiceId == rightMicroservice.ServiceId;
        }

        return left.Type == right.Type && Equals(left, right);
    }

    private static bool ForwardingEqual(
        ForwardingConfiguration left,
        ForwardingConfiguration right) =>
        left.Mode == right.Mode &&
        string.Equals(left.ReplaceTemplate, right.ReplaceTemplate, StringComparison.Ordinal);

    private static bool HeaderRewritesEqual(
        ImmutableArray<HeaderRewriteConfiguration> left,
        ImmutableArray<HeaderRewriteConfiguration> right) =>
        left.Length == right.Length && left.SequenceEqual(right, HeaderRewriteComparer.Instance);

    private sealed class HeaderRewriteComparer : IEqualityComparer<HeaderRewriteConfiguration>
    {
        public static HeaderRewriteComparer Instance { get; } = new();

        public bool Equals(HeaderRewriteConfiguration? left, HeaderRewriteConfiguration? right) =>
            left is not null && right is not null &&
            left.Operation == right.Operation &&
            string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            string.Equals(left.Value, right.Value, StringComparison.Ordinal);

        public int GetHashCode(HeaderRewriteConfiguration obj) =>
            HashCode.Combine(obj.Operation, obj.Name, obj.Value);
    }

    private sealed record OrphanSweep(
        HashSet<Guid> ServiceIds,
        HashSet<Guid> RouteIds,
        ImmutableArray<string> Notes);

    private static OrphanSweep FindOrphanSweep(
        HostConfigurationSnapshot snapshot,
        IReadOnlySet<Guid> lockServiceIds,
        IReadOnlySet<Guid> desiredServiceIds)
    {
        var candidates = new HashSet<Guid>();
        foreach (var route in snapshot.Routes)
        {
            if (!IsOwnedRoute(route.MetadataJson) ||
                route.Target is not MicroserviceRouteTargetConfiguration microservice ||
                lockServiceIds.Contains(microservice.ServiceId) ||
                desiredServiceIds.Contains(microservice.ServiceId))
            {
                continue;
            }

            candidates.Add(microservice.ServiceId);
        }

        var orphanServices = new HashSet<Guid>();
        var orphanRoutes = new HashSet<Guid>();
        var notes = ImmutableArray.CreateBuilder<string>();
        foreach (var serviceId in candidates)
        {
            if (!snapshot.Services.Any(service => service.Id == serviceId))
            {
                continue;
            }

            var references = snapshot.Routes
                .Where(route =>
                    route.Target is MicroserviceRouteTargetConfiguration microservice &&
                    microservice.ServiceId == serviceId)
                .ToArray();
            var ownedReferences = references.Where(route => IsOwnedRoute(route.MetadataJson)).ToArray();
            if (ownedReferences.Length == 0 || references.Any(route => !IsOwnedRoute(route.MetadataJson)))
            {
                // A non-svchost or otherwise unmanaged route protects the service.
                continue;
            }

            orphanServices.Add(serviceId);
            foreach (var route in ownedReferences)
            {
                orphanRoutes.Add(route.Id);
            }

            var note = $"Swept orphan service {serviceId} and {ownedReferences.Length} svchost-owned route(s).";
            notes.Add(note);
        }

        return new OrphanSweep(orphanServices, orphanRoutes, notes.ToImmutable());
    }

    private static bool IsOwnedRoute(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("owner", out var owner) ||
                owner.ValueKind != JsonValueKind.String ||
                !string.Equals(owner.GetString(), Owner, StringComparison.Ordinal))
            {
                return false;
            }

            return document.RootElement.TryGetProperty("config", out var config) &&
                   config.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(config.GetString()) &&
                   document.RootElement.TryGetProperty("service", out var service) &&
                   service.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(service.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
