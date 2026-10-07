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

    [Fact]
    public async Task Reconcile_prunes_previous_generation_when_service_confirmed_running()
    {
        // Pruning is continuous: any committed reconcile prunes older unreferenced generations of a
        // confirmed-running service; this scenario also changes the source, so the previous generation is
        // pruned in the same post-commit pass.
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            var serviceRoot = Path.Combine(fixture.DataDirectory, "svchost", "demo");
            var supersededArtifactPath = GetContentAddressedArtifactPath(serviceRoot, "api", fixture.SourcePath);
            var service = Assert.Single(fixture.Snapshot.Services);
            var supervisorApi = FixedRuntimeSupervisorProxy.Create();
            var supervisor = (FixedRuntimeSupervisorProxy)(object)supervisorApi;
            var full = new FakeFullConfigurationApi(fixture.Snapshot)
            {
                BeforeReplace = _ => supervisor.Snapshots =
                [
                    FixedRuntimeSupervisorProxy.CreateRuntimeSnapshot(
                        service.Id,
                        ExtensionServiceLifecycleState.Running,
                        DateTimeOffset.UtcNow)
                ]
            };
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory, supervisorApi);

            await File.WriteAllTextAsync(fixture.SourcePath, "api-v2");
            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            var committedArtifactPath = Assert.Single(full.Snapshot.Services).FileName;
            Assert.Equal(
                GetContentAddressedArtifactPath(serviceRoot, "api", fixture.SourcePath),
                committedArtifactPath);
            Assert.False(Directory.Exists(Path.GetDirectoryName(supersededArtifactPath)));
            Assert.True(Directory.Exists(Path.GetDirectoryName(committedArtifactPath)));
            Assert.True(File.Exists(committedArtifactPath));
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_retains_superseded_generation_when_started_at_is_older_than_commit()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            var serviceRoot = Path.Combine(fixture.DataDirectory, "svchost", "demo");
            var supersededArtifactPath = GetContentAddressedArtifactPath(serviceRoot, "api", fixture.SourcePath);
            var service = Assert.Single(fixture.Snapshot.Services);
            var supervisor = FixedRuntimeSupervisorProxy.Create(
                FixedRuntimeSupervisorProxy.CreateRuntimeSnapshot(
                    service.Id,
                    ExtensionServiceLifecycleState.Running,
                    service.UpdatedAt));
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory, supervisor);

            await File.WriteAllTextAsync(fixture.SourcePath, "api-v2");
            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            var committedArtifactPath = Assert.Single(full.Snapshot.Services).FileName;
            Assert.Equal(
                GetContentAddressedArtifactPath(serviceRoot, "api", fixture.SourcePath),
                committedArtifactPath);
            Assert.True(Directory.Exists(Path.GetDirectoryName(supersededArtifactPath)));
            Assert.True(Directory.Exists(Path.GetDirectoryName(committedArtifactPath)));
            Assert.True(File.Exists(committedArtifactPath));
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_retains_superseded_generation_when_started_at_unknown()
    {
        var fixture = await CreateReusablePathFixtureAsync();
        try
        {
            var serviceRoot = Path.Combine(fixture.DataDirectory, "svchost", "demo");
            var supersededArtifactPath = GetContentAddressedArtifactPath(serviceRoot, "api", fixture.SourcePath);
            var service = Assert.Single(fixture.Snapshot.Services);
            var supervisor = FixedRuntimeSupervisorProxy.Create(
                FixedRuntimeSupervisorProxy.CreateRuntimeSnapshot(
                    service.Id,
                    ExtensionServiceLifecycleState.Running));
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory, supervisor);

            await File.WriteAllTextAsync(fixture.SourcePath, "api-v2");
            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            var committedArtifactPath = Assert.Single(full.Snapshot.Services).FileName;
            Assert.Equal(
                GetContentAddressedArtifactPath(serviceRoot, "api", fixture.SourcePath),
                committedArtifactPath);
            Assert.True(Directory.Exists(Path.GetDirectoryName(supersededArtifactPath)));
            Assert.True(Directory.Exists(Path.GetDirectoryName(committedArtifactPath)));
            Assert.True(File.Exists(committedArtifactPath));
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_prunes_stale_generation_without_source_change_when_service_confirmed_running()
    {
        var fixture = await CreateReusablePathFixtureAsync(changedSnapshot: true);
        try
        {
            var serviceRoot = Path.Combine(fixture.DataDirectory, "svchost", "demo");
            var artifactPath = GetContentAddressedArtifactPath(serviceRoot, "api", fixture.SourcePath);
            var staleGenerationDirectory = Path.Combine(
                serviceRoot,
                "artifacts",
                "sha256",
                "api",
                new string('a', 64));
            Directory.CreateDirectory(staleGenerationDirectory);
            await File.WriteAllTextAsync(Path.Combine(staleGenerationDirectory, "api"), "api-stale");
            var service = Assert.Single(fixture.Snapshot.Services);
            var supervisorApi = FixedRuntimeSupervisorProxy.Create();
            var supervisor = (FixedRuntimeSupervisorProxy)(object)supervisorApi;
            var full = new FakeFullConfigurationApi(fixture.Snapshot)
            {
                BeforeReplace = _ => supervisor.Snapshots =
                [
                    FixedRuntimeSupervisorProxy.CreateRuntimeSnapshot(
                        service.Id,
                        ExtensionServiceLifecycleState.Running,
                        DateTimeOffset.UtcNow)
                ]
            };
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory, supervisorApi);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            var committedArtifactPath = Assert.Single(full.Snapshot.Services).FileName;
            Assert.Equal(artifactPath, committedArtifactPath);
            // The in-use generation stays protected by the Host service path and the lock digest; only the
            // stale unreferenced generation is pruned.
            Assert.False(Directory.Exists(staleGenerationDirectory));
            Assert.True(Directory.Exists(Path.GetDirectoryName(artifactPath)));
            Assert.True(File.Exists(artifactPath));
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_sweeps_inactive_and_former_roots_after_commit()
    {
        var fixture = await CreateReusablePathFixtureAsync(changedSnapshot: true);
        try
        {
            var serviceRoot = Path.Combine(fixture.DataDirectory, "svchost", "demo");
            var artifactPath = GetContentAddressedArtifactPath(serviceRoot, "api", fixture.SourcePath);
            var inactiveRoot = Path.Combine(fixture.DataDirectory, "svchost", "legacy");
            var pinnedArtifactPath = GetContentAddressedArtifactPath(inactiveRoot, "api", fixture.SourcePath);
            var inactiveStaleDirectory = Path.Combine(
                inactiveRoot,
                "artifacts",
                "sha256",
                "api",
                new string('b', 64));
            var formerRoot = Path.Combine(fixture.DataDirectory, "svchost", "former");
            var formerStaleDirectory = Path.Combine(
                formerRoot,
                "artifacts",
                "sha256",
                "api",
                new string('c', 64));
            Directory.CreateDirectory(Path.GetDirectoryName(pinnedArtifactPath)!);
            File.Copy(fixture.SourcePath, pinnedArtifactPath);
            foreach (var staleDirectory in new[] { inactiveStaleDirectory, formerStaleDirectory })
            {
                Directory.CreateDirectory(staleDirectory);
                await File.WriteAllTextAsync(Path.Combine(staleDirectory, "api"), "api-stale");
            }

            var inactiveSettings = CreatePathSettings(
                fixture.SourcePath,
                Guid.CreateVersion7(),
                stopped: ["api"]);
            var inactiveServiceId = inactiveSettings.Configs["demo"]!.Lock.Services["api"].ServiceId;
            var settings = CloneSettingsWithInactiveRoot(fixture.Settings, inactiveSettings.Configs["demo"]);
            var service = Assert.Single(fixture.Snapshot.Services);
            var supervisorApi = FixedRuntimeSupervisorProxy.Create();
            var supervisor = (FixedRuntimeSupervisorProxy)(object)supervisorApi;
            var full = new FakeFullConfigurationApi(fixture.Snapshot)
            {
                BeforeReplace = _ => supervisor.Snapshots =
                [
                    FixedRuntimeSupervisorProxy.CreateRuntimeSnapshot(
                        service.Id,
                        ExtensionServiceLifecycleState.Running,
                        DateTimeOffset.UtcNow),
                    FixedRuntimeSupervisorProxy.CreateRuntimeSnapshot(
                        inactiveServiceId,
                        ExtensionServiceLifecycleState.Disabled)
                ]
            };
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory, supervisorApi);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.False(Directory.Exists(inactiveStaleDirectory));
            Assert.False(Directory.Exists(formerStaleDirectory));
            Assert.True(Directory.Exists(Path.GetDirectoryName(pinnedArtifactPath)));
            Assert.True(File.Exists(pinnedArtifactPath));
            Assert.True(File.Exists(artifactPath));
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    private static SvchostSettings CloneSettingsWithInactiveRoot(
        SvchostSettings settings,
        SvchostConfigSettings inactiveConfig)
    {
        var configs = new Dictionary<string, SvchostConfigSettings>(settings.Configs, StringComparer.Ordinal)
        {
            ["legacy"] = inactiveConfig
        };
        return new SvchostSettings(
            settings.ApiKey,
            settings.Routes,
            configs,
            settings.ReleaseProviders,
            settings.Retiring);
    }
}
