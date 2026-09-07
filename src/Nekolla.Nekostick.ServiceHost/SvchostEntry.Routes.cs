using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.ServiceHost.Api;
using Nekolla.Nekostick.ServiceHost.Settings;
using Nekolla.Nekostick.ServiceHost.Webui;

namespace Nekolla.Nekostick.ServiceHost;

public sealed partial class SvchostEntry
{
    private static async ValueTask<bool> UpsertHandlerRoutesAsync(
        IExtensionHostBridge13 bridge,
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

    private static async ValueTask<bool> UpsertRouteAsync(
        IExtensionHostBridge13 bridge,
        ExtensionRouteConfiguration route,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var read = await bridge.ConfigurationApi.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess || read.Value is null)
            {
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

            if (!write.Errors.Any(error => error.Code == ConfigurationErrorCode.ConcurrencyConflict))
            {
                return false;
            }
        }

        return false;
    }
}
