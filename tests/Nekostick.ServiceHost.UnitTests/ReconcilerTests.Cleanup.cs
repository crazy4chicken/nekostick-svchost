using System.Security.Cryptography;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed partial class ReconcilerTests
{
    [Fact]
    public async Task Reconcile_removes_retired_generation_only_after_Host_removal_commit_and_keeps_logs()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "api.bin");
            await File.WriteAllTextAsync(sourcePath, "api-v1");
            var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)))
                .ToLowerInvariant();
            var dataDirectory = Path.Combine(root, "data");
            var serviceRoot = Path.Combine(dataDirectory, "svchost", "demo");
            var artifactPath = Path.Combine(serviceRoot, "artifacts", "sha256", "api", digest, "api");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.Copy(sourcePath, artifactPath);
            var logPath = Path.Combine(serviceRoot, "logs", "api.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            await File.WriteAllTextAsync(logPath, "retained log");

            var serviceId = Guid.CreateVersion7();
            var inactiveRuntime = new ExtensionServiceRuntimeSnapshot(
                serviceId,
                null,
                null,
                null,
                ExtensionServiceLifecycleState.Disabled,
                ExtensionServiceHealthState.Unknown,
                0,
                0,
                null,
                null,
                null);
            var settings = new SvchostSettings(
                null,
                new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()),
                new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal)
                {
                    ["demo"] = new SvchostConfigSettings(
                        "serviceScope: document\nservices: {}",
                        new LockModel
                        {
                            Services = new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal)
                            {
                                ["api"] = new LockServiceEntry(
                                    new LockSource { Kind = "path", Path = sourcePath, Sha256 = digest },
                                    serviceId)
                            }
                        })
                });
            var service = CreateService(serviceId, true, artifactPath, serviceRoot, DateTimeOffset.UtcNow);
            var full = new FakeFullConfigurationApi(CreateSnapshot([service]));
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(settings)),
                full,
                dataDirectory,
                FixedRuntimeSupervisorProxy.Create(inactiveRuntime));

            var pending = await reconciler.ReconcileAsync("test");

            Assert.False(pending.Succeeded);
            Assert.Equal(SyncErrorCode.RemovalPending, pending.FailureCode);
            Assert.True(File.Exists(artifactPath));

            var completed = await reconciler.ReconcileAsync("test");

            Assert.True(completed.Succeeded);
            Assert.Empty(full.Snapshot.Services);
            Assert.False(File.Exists(artifactPath));
            Assert.Equal("retained log", await File.ReadAllTextAsync(logPath));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Reconcile_deleted_document_config_cleans_artifacts_and_tmp_only_after_stage_b_commit()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "api.bin");
            await File.WriteAllTextAsync(sourcePath, "api-v1");
            var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)))
                .ToLowerInvariant();
            var dataDirectory = Path.Combine(root, "data");
            var serviceRoot = Path.Combine(dataDirectory, "svchost", "demo");
            var artifactPath = Path.Combine(serviceRoot, "artifacts", "sha256", "api", digest, "api");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.Copy(sourcePath, artifactPath);
            var temporaryPath = Path.Combine(serviceRoot, "tmp", "partial-download");
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
            await File.WriteAllTextAsync(temporaryPath, "incomplete artifact");
            var logPath = Path.Combine(serviceRoot, "logs", "api.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            await File.WriteAllTextAsync(logPath, "retained log");
            var userDataPath = Path.Combine(serviceRoot, "user-data.json");
            await File.WriteAllTextAsync(userDataPath, "retained data");

            var serviceId = Guid.CreateVersion7();
            var routeId = Guid.CreateVersion7();
            var retirement = new RetiringServiceSettings(
                serviceId,
                [routeId],
                "demo",
                "api",
                "document");
            var settings = new SvchostSettings(
                null,
                new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()),
                retiring: [retirement]);
            var inactiveRuntime = new ExtensionServiceRuntimeSnapshot(
                serviceId,
                null,
                null,
                null,
                ExtensionServiceLifecycleState.Disabled,
                ExtensionServiceHealthState.Unknown,
                0,
                0,
                null,
                null,
                null);
            var service = CreateService(serviceId, true, artifactPath, serviceRoot, DateTimeOffset.UtcNow);
            var route = CreateRoute(
                routeId,
                serviceId,
                "/api",
                "{\"owner\":\"nekostick.svchost\",\"config\":\"demo\",\"service\":\"api\"}");
            var full = new FakeFullConfigurationApi(CreateSnapshot([service], [route]));
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(settings));
            var reconciler = CreateReconciler(
                configurationApi,
                full,
                dataDirectory,
                FixedRuntimeSupervisorProxy.Create(inactiveRuntime));

            var pending = await reconciler.ReconcileAsync("test");

            Assert.False(pending.Succeeded);
            Assert.Equal(SyncErrorCode.RemovalPending, pending.FailureCode);
            Assert.False(Assert.Single(full.Snapshot.Services).Enabled);
            Assert.Empty(full.Snapshot.Routes);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.True(File.Exists(artifactPath));
            Assert.True(File.Exists(temporaryPath));
            Assert.True(File.Exists(logPath));
            Assert.True(File.Exists(userDataPath));

            var completed = await reconciler.ReconcileAsync("test");

            Assert.True(completed.Succeeded);
            Assert.Empty(full.Snapshot.Services);
            Assert.Empty(full.Snapshot.Routes);
            Assert.Equal(2, full.ReplaceCallCount);
            Assert.False(File.Exists(artifactPath));
            Assert.False(Directory.Exists(Path.Combine(serviceRoot, "artifacts")));
            Assert.False(Directory.Exists(Path.Combine(serviceRoot, "tmp")));
            Assert.Equal("retained log", await File.ReadAllTextAsync(logPath));
            Assert.Equal("retained data", await File.ReadAllTextAsync(userDataPath));

            var persisted = await new SettingsStore(configurationApi).ReadSettingsAsync();
            Assert.True(persisted.IsSuccess);
            Assert.Empty(persisted.Value!.Settings!.Retiring);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }
}
