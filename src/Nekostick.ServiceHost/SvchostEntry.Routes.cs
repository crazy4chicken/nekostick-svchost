using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Api;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Webui;

namespace Nekostick.ServiceHost;

public sealed partial class SvchostEntry
{
    private static async ValueTask<bool> UpsertHandlerRoutesAsync(
        IExtensionHostBridge14 bridge,
        SvchostRouteSettings routeSettings,
        CancellationToken cancellationToken)
    {
        var apiRoute = new ExtensionRouteConfiguration(
            routeSettings.Api,
            true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Prefix,
                "/svchost/api",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            new ExtensionHandlerRouteTarget(SvchostApiHandler.StableHandlerId),
            100);
        var webuiRoute = new ExtensionRouteConfiguration(
            routeSettings.Webui,
            true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Prefix,
                "/svchost",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            new ExtensionHandlerRouteTarget(WebuiHandler.StableHandlerId),
            10);
        return await UpsertRouteAsync(bridge, apiRoute, cancellationToken).ConfigureAwait(false) &&
               await UpsertRouteAsync(bridge, webuiRoute, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> RemoveStaleHandlerRoutesAsync(
        IExtensionHostBridge14 bridge,
        SvchostRouteSettings routeSettings,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var read = await bridge.ConfigurationApi.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess || read.Value is null)
            {
                ReportConfigurationErrors(bridge, read.Errors, "read routes for cleanup");
                return false;
            }

            var staleRouteIds = read.Value.Routes
                .Where(route =>
                    route.Target is ExtensionHandlerRouteTarget &&
                    route.Id != routeSettings.Api &&
                    route.Id != routeSettings.Webui)
                .Select(route => route.Id)
                .ToImmutableArray();
            if (staleRouteIds.IsDefaultOrEmpty)
            {
                return true;
            }

            var changes = new ExtensionConfigurationChangeSet(
                ImmutableArray<ExtensionRouteConfiguration>.Empty,
                staleRouteIds,
                ImmutableArray<ExtensionServiceConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                null);
            var write = await bridge.ConfigurationApi.ApplyAsync(
                    read.Value.Version,
                    changes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (write.IsSuccess)
            {
                return true;
            }

            ReportConfigurationErrors(bridge, write.Errors, "remove stale handler routes");
            if (!write.Errors.Any(error => error.Code == ConfigurationErrorCode.ConcurrencyConflict))
            {
                return false;
            }
        }

        return false;
    }

    private static async ValueTask<bool> UpsertRouteAsync(
        IExtensionHostBridge14 bridge,
        ExtensionRouteConfiguration route,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var read = await bridge.ConfigurationApi.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess || read.Value is null)
            {
                ReportConfigurationErrors(bridge, read.Errors, "read routes for registration");
                return false;
            }

            var write = await bridge.Routes.UpsertAsync(
                    read.Value.Version,
                    route,
                    cancellationToken)
                .ConfigureAwait(false);
            if (write.IsSuccess)
            {
                return true;
            }

            ReportConfigurationErrors(bridge, write.Errors, $"upsert handler route '{route.Id}'");
            if (!write.Errors.Any(error => error.Code == ConfigurationErrorCode.ConcurrencyConflict))
            {
                return false;
            }
        }

        return false;
    }

    private static void ReportConfigurationErrors(
        IExtensionHostBridge14 bridge,
        IEnumerable<ConfigurationError> errors,
        string operation)
    {
        foreach (var error in errors)
        {
            WriteContractFailureBestEffort(
                bridge,
                $"Configuration operation '{operation}' failed with {error.Code}: {error.Message}");
        }
    }
}
