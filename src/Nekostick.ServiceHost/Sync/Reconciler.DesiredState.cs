using System.Collections.Immutable;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;

namespace Nekostick.ServiceHost.Sync;

public sealed partial class Reconciler
{
    private sealed record DesiredStatePlan(
        List<ServiceSyncReport> Reports,
        List<DesiredServiceState> Desired,
        HashSet<Guid> ManagedServiceIds,
        HashSet<Guid> ManagedRouteIds,
        HashSet<Guid> ConfiguredLockServiceIds,
        HashSet<Guid> SourceFailureServiceIds,
        HashSet<Guid> SourceFailureRouteIds,
        Dictionary<(string ConfigName, string ServiceName), LockServiceEntry> UpdatedLocks,
        Dictionary<string, ImmutableHashSet<string>> ConfigServiceNames,
        Dictionary<string, string> ConfigYamls,
        bool ParseFailed);

    private async ValueTask<DesiredStatePlan> BuildDesiredStateAsync(
        SvchostSettings settings,
        IReadOnlySet<Guid> extraManagedServiceIds,
        IReadOnlySet<Guid> extraManagedRouteIds,
        CancellationToken cancellationToken)
    {
        var reports = new List<ServiceSyncReport>();
        var desired = new List<DesiredServiceState>();
        var managedServiceIds = new HashSet<Guid>();
        var managedRouteIds = new HashSet<Guid>();
        var configuredLockServiceIds = new HashSet<Guid>();
        var sourceFailureServiceIds = new HashSet<Guid>();
        var sourceFailureRouteIds = new HashSet<Guid>();
        var updatedLocks = new Dictionary<(string ConfigName, string ServiceName), LockServiceEntry>();
        var configServiceNames = new Dictionary<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        var configYamls = new Dictionary<string, string>(StringComparer.Ordinal);
        var parseFailed = false;
        managedServiceIds.UnionWith(extraManagedServiceIds);
        managedRouteIds.UnionWith(extraManagedRouteIds);

        foreach (var configPair in settings.Configs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configName = configPair.Key;
            var config = configPair.Value;
            if (config is null)
            {
                parseFailed = true;
                reports.Add(new ServiceSyncReport(
                    configName,
                    string.Empty,
                    false,
                    false,
                    null,
                    ImmutableArray<Guid>.Empty,
                    "The configuration entry is null.",
                    SyncErrorCode.ReconcileFailed));
                continue;
            }

            var lockServices = config.Lock?.Services ?? new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal);
            foreach (var oldLock in lockServices.Values)
            {
                if (oldLock is null)
                {
                    continue;
                }
                if (IsUuidV7(oldLock.ServiceId))
                {
                    managedServiceIds.Add(oldLock.ServiceId);
                    configuredLockServiceIds.Add(oldLock.ServiceId);
                }

                foreach (var routeId in oldLock.RouteIds ?? new List<Guid>())
                {
                    if (IsUuidV7(routeId))
                    {
                        managedRouteIds.Add(routeId);
                    }
                }
            }

            ComposeFile compose;
            try
            {
                compose = _composeFileParser.Parse(config.Yaml ?? string.Empty);
            }
            catch (ComposeValidationException exception)
            {
                parseFailed = true;
                reports.AddRange(
                    exception.Errors.Select(error => new ServiceSyncReport(
                        configName,
                        ServiceNameFromPath(error.Path),
                        false,
                        false,
                        null,
                        ImmutableArray<Guid>.Empty,
                        error.Message,
                        SyncErrorCode.ReconcileFailed)));
                continue;
            }
            configServiceNames[configName] = compose.Services.Keys.ToImmutableHashSet(StringComparer.Ordinal);
            configYamls[configName] = config.Yaml ?? string.Empty;

            var serviceStates = new List<(
                string ServiceName,
                ComposeService ComposeService,
                LockServiceEntry? PreviousLock,
                SourceResolutionResult Resolution,
                Guid? ServiceId)>();
            foreach (var servicePair in compose.Services.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var serviceName = servicePair.Key;
                var composeService = servicePair.Value;
                lockServices.TryGetValue(serviceName, out var previousLock);
                var resolved = await _sourceResolver.ResolveAsync(
                        _dataDirectory,
                        configName,
                        serviceName,
                        composeService.Source,
                        previousLock?.Source,
                        settings.ReleaseProviders,
                        cancellationToken)
                    .ConfigureAwait(false);
                var serviceId = previousLock is not null && IsUuidV7(previousLock.ServiceId)
                    ? previousLock.ServiceId
                    : resolved.Succeeded && resolved.Source is not null && resolved.ArtifactPath is not null
                        ? Guid.CreateVersion7()
                        : (Guid?)null;
                serviceStates.Add((serviceName, composeService, previousLock, resolved, serviceId));
            }

            var serviceIds = serviceStates
                .Where(state => state.ServiceId is not null)
                .ToDictionary(state => state.ServiceName, state => state.ServiceId!.Value, StringComparer.Ordinal);
            Func<string, Guid?> resolveTemplateTarget = target =>
                serviceIds.TryGetValue(target, out var serviceId) ? serviceId : null;

            foreach (var state in serviceStates)
            {
                var serviceName = state.ServiceName;
                var composeService = state.ComposeService;
                var previousLock = state.PreviousLock;
                var resolved = state.Resolution;
                if (!resolved.Succeeded || resolved.Source is null || resolved.ArtifactPath is null)
                {
                    var failedServiceId = state.ServiceId ?? Guid.Empty;
                    var failedRouteIds = previousLock?.RouteIds?.ToImmutableArray() ?? ImmutableArray<Guid>.Empty;
                    if (failedServiceId != Guid.Empty)
                    {
                        sourceFailureServiceIds.Add(failedServiceId);
                        foreach (var routeId in failedRouteIds.Where(IsUuidV7))
                        {
                            sourceFailureRouteIds.Add(routeId);
                        }
                    }

                    reports.Add(new ServiceSyncReport(
                        configName,
                        serviceName,
                        false,
                        false,
                        previousLock?.ServiceId,
                        failedRouteIds,
                        resolved.Error ?? "The source could not be resolved.",
                        SyncErrorCode.SourceFailed)
                    {
                        Warnings = composeService.Warnings.AddRange(resolved.Warnings),
                        NodeLocal = failedServiceId != Guid.Empty
                    });
                    continue;
                }

                var serviceId = state.ServiceId!.Value;
                ImmutableArray<string> args;
                ImmutableDictionary<string, string> environment;
                try
                {
                    args = composeService.Args
                        .Select(value => ComposeTemplate.Rewrite(value, resolveTemplateTarget))
                        .ToImmutableArray();
                    environment = composeService.Environment.ToImmutableDictionary(
                        pair => pair.Key,
                        pair => ComposeTemplate.Rewrite(pair.Value, resolveTemplateTarget),
                        StringComparer.Ordinal);
                }
                catch (ComposeTemplateResolutionException exception)
                {
                    var failedServiceId = previousLock is not null && IsUuidV7(previousLock.ServiceId)
                        ? previousLock.ServiceId
                        : Guid.Empty;
                    var failedRouteIds = previousLock?.RouteIds?.ToImmutableArray() ?? ImmutableArray<Guid>.Empty;
                    if (failedServiceId != Guid.Empty)
                    {
                        sourceFailureServiceIds.Add(failedServiceId);
                        foreach (var routeId in failedRouteIds.Where(IsUuidV7))
                        {
                            sourceFailureRouteIds.Add(routeId);
                        }
                    }

                    reports.Add(new ServiceSyncReport(
                        configName,
                        serviceName,
                        false,
                        false,
                        previousLock?.ServiceId,
                        failedRouteIds,
                        $"The template target service '{exception.Target}' could not be resolved.",
                        SyncErrorCode.ReconcileFailed)
                    {
                        Warnings = composeService.Warnings.AddRange(resolved.Warnings)
                    });
                    continue;
                }

                var routeIds = ResolveRouteIds(composeService.Route, previousLock);
                var workingDirectory = Path.GetFullPath(Path.Combine(_dataDirectory, "svchost", configName));
                var now = DateTimeOffset.UtcNow;
                var isStopped = config.Stopped?.Contains(serviceName, StringComparer.Ordinal) == true;
                var serviceConfiguration = new ServiceConfiguration(
                    serviceId,
                    !isStopped,
                    resolved.ArtifactPath,
                    args,
                    workingDirectory,
                    environment,
                    ToContract(composeService.Start),
                    ToContract(composeService.Restart),
                    new ServiceHealthCheckConfiguration(
                        ToContract(composeService.Health.Type),
                        composeService.Health.Path,
                        composeService.Health.Timeout),
                    now,
                    now,
                    0);
                var routeConfiguration = composeService.Route is null
                    ? null
                    : CreateRouteConfiguration(
                        composeService.Route,
                        serviceId,
                        routeIds[0],
                        configName,
                        serviceName,
                        now);
                var lockEntry = new LockServiceEntry(resolved.Source, serviceId, routeIds);
                desired.Add(new DesiredServiceState(
                    configName,
                    serviceName,
                    serviceConfiguration,
                    routeConfiguration,
                    lockEntry,
                    resolved.Reused));
                updatedLocks[(configName, serviceName)] = lockEntry;
                managedServiceIds.Add(serviceId);
                foreach (var routeId in routeIds)
                {
                    managedRouteIds.Add(routeId);
                }
                reports.Add(new ServiceSyncReport(
                    configName,
                    serviceName,
                    true,
                    !resolved.Reused,
                    serviceId,
                    routeIds.ToImmutableArray(),
                    null)
                {
                    Warnings = composeService.Warnings.AddRange(resolved.Warnings)
                });
            }
        }

        return new DesiredStatePlan(
            reports,
            desired,
            managedServiceIds,
            managedRouteIds,
            configuredLockServiceIds,
            sourceFailureServiceIds,
            sourceFailureRouteIds,
            updatedLocks,
            configServiceNames,
            configYamls,
            parseFailed);
    }

    private static RouteConfiguration CreateRouteConfiguration(
        ComposeRoute route,
        Guid serviceId,
        Guid routeId,
        string configName,
        string serviceName,
        DateTimeOffset now)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            owner = Owner,
            config = configName,
            service = serviceName
        });
        return new RouteConfiguration(
            routeId,
            true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Prefix,
                route.Prefix,
                route.Hosts,
                route.Methods),
            new MicroserviceRouteTargetConfiguration(serviceId),
            0,
            new ForwardingConfiguration(
                route.Strip ? ForwardingMode.Strip : ForwardingMode.Preserve,
                null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            metadata,
            now,
            now,
            0);
    }

    private static List<Guid> ResolveRouteIds(ComposeRoute? route, LockServiceEntry? previous)
    {
        if (route is null)
        {
            return new List<Guid>();
        }

        var previousId = previous?.RouteIds?.FirstOrDefault(id => IsUuidV7(id)) ?? Guid.Empty;
        return new List<Guid> { previousId == Guid.Empty ? Guid.CreateVersion7() : previousId };
    }

    private static ServiceStartMode ToContract(ComposeStartMode mode) => mode switch
    {
        ComposeStartMode.Lazy => ServiceStartMode.Lazy,
        _ => ServiceStartMode.Eager
    };

    private static ServiceRestartPolicy ToContract(ComposeRestartPolicy policy) => policy switch
    {
        ComposeRestartPolicy.Never => ServiceRestartPolicy.Never,
        ComposeRestartPolicy.Always => ServiceRestartPolicy.Always,
        _ => ServiceRestartPolicy.OnFailure
    };

    private static ServiceHealthCheckType ToContract(ComposeHealthCheckType type) => type switch
    {
        ComposeHealthCheckType.Tcp => ServiceHealthCheckType.Tcp,
        ComposeHealthCheckType.Http => ServiceHealthCheckType.Http,
        _ => ServiceHealthCheckType.Process
    };

    private static string ServiceNameFromPath(string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private static bool IsUuidV7(Guid value)
    {
        if (value == Guid.Empty)
        {
            return false;
        }

        var bytes = value.ToByteArray();
        return (bytes[7] & 0xF0) == 0x70;
    }
}
