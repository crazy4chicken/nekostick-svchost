using Nekostick.ServiceHost.Compose;

namespace Nekostick.ServiceHost.Sync;

internal static class ServiceRootPath
{
    public static string Resolve(string dataDirectory, ComposeServiceScope serviceScope, string configName)
    {
        var directoryName = serviceScope switch
        {
            ComposeServiceScope.Global => "global",
            ComposeServiceScope.Document => configName,
            _ => throw new ArgumentOutOfRangeException(nameof(serviceScope), serviceScope, "Unknown service scope.")
        };

        return Path.GetFullPath(Path.Combine(dataDirectory, "svchost", directoryName));
    }
}
