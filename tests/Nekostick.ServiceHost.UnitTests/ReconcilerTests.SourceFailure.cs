using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed partial class ReconcilerTests
{
    [Fact]
    public async Task Reconcile_source_failure_retains_deployed_service_and_owned_routes_as_node_local()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "api.bin");
            await File.WriteAllTextAsync(sourcePath, "api-v1");
            var dataDirectory = Path.Combine(root, "data");
            var artifactPath = Path.Combine(dataDirectory, "svchost", "demo", "artifacts", "api");
            var workingDirectory = Path.Combine(dataDirectory, "svchost", "demo");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.Copy(sourcePath, artifactPath);

            var serviceId = Guid.CreateVersion7();
            var routeId = Guid.CreateVersion7();
            var settings = CreatePathSettings(sourcePath, serviceId);
            settings.Configs["demo"]!.Yaml = $"""
                serviceScope: document
                services:
                  api:
                    source:
                      path: {sourcePath}
                    route:
                      prefix: /api
                """;
            settings.Configs["demo"]!.Lock!.Services["api"].RouteIds = [routeId];
            File.Delete(sourcePath);

            var service = CreateService(
                serviceId,
                true,
                artifactPath,
                workingDirectory,
                DateTimeOffset.UtcNow);
            var route = CreateRoute(
                routeId,
                serviceId,
                "/api",
                "{\"owner\":\"nekostick.svchost\",\"config\":\"demo\",\"service\":\"api\"}");
            var full = new FakeFullConfigurationApi(CreateSnapshot([service], [route]));
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(settings)),
                full,
                dataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.False(report.Succeeded);
            var serviceReport = Assert.Single(report.Services);
            Assert.False(serviceReport.Succeeded);
            Assert.Equal(SyncErrorCode.SourceFailed, serviceReport.FailureCode);
            Assert.Equal(serviceId, serviceReport.ServiceId);
            Assert.True(serviceReport.NodeLocal);
            Assert.Equal(ServiceDecision.Preserved, serviceReport.Decision);
            Assert.Equal([routeId], serviceReport.RouteIds.ToArray());
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Contains(full.Snapshot.Services, candidate => candidate.Id == serviceId);
            Assert.Contains(full.Snapshot.Routes, candidate => candidate.Id == routeId);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Reconcile_never_deployed_source_failure_does_not_add_service_to_snapshot()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "missing.bin");
            var yaml = $"""
                services:
                  api:
                    source:
                      path: {sourcePath}
                """;
            var settings = new SvchostSettings(
                null,
                new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()),
                new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal)
                {
                    ["demo"] = new SvchostConfigSettings(yaml, new LockModel())
                });
            var full = new FakeFullConfigurationApi(CreateSnapshot());
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(settings)),
                full,
                root);

            var report = await reconciler.ReconcileAsync("test");

            Assert.False(report.Succeeded);
            var serviceReport = Assert.Single(report.Services);
            Assert.False(serviceReport.Succeeded);
            Assert.Equal(SyncErrorCode.SourceFailed, serviceReport.FailureCode);
            Assert.Null(serviceReport.ServiceId);
            Assert.False(serviceReport.NodeLocal);
            Assert.Equal(ServiceDecision.Failed, serviceReport.Decision);
            Assert.Empty(full.Snapshot.Services);
            Assert.Empty(full.Snapshot.Routes);
            Assert.Equal(0, full.ReplaceCallCount);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }
}
