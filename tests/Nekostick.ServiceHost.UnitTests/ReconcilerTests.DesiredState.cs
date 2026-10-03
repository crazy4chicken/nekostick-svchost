using System.Collections.Immutable;
using System.Security.Cryptography;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed partial class ReconcilerTests
{
    [Fact]
    public async Task Reconcile_changed_desired_state_calls_replace()
    {
        var fixture = await CreateReusablePathFixtureAsync(changedSnapshot: true);
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(fixture.Settings));
            var reconciler = CreateReconciler(configurationApi, full, fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Equal("test", report.Trigger);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(fixture.Snapshot.Version + 1, report.WrittenConfigurationVersion);
            Assert.Equal(0, configurationApi.WriteSettingsCallCount);
            Assert.Equal(["--changed"], full.LastChanges!.Services.Single().ArgumentList.ToArray());
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_rewrites_service_template_targets_and_reuses_lock_ids()
    {
        var root = CreateTempDirectory();
        try
        {
            var apiSourcePath = Path.Combine(root, "api.bin");
            var dbSourcePath = Path.Combine(root, "db.bin");
            await File.WriteAllTextAsync(apiSourcePath, "api-v1");
            await File.WriteAllTextAsync(dbSourcePath, "db-v1");

            var dataDirectory = Path.Combine(root, "data");
            var artifactDirectory = Path.Combine(dataDirectory, "svchost", "demo", "artifacts");
            Directory.CreateDirectory(artifactDirectory);
            File.Copy(apiSourcePath, Path.Combine(artifactDirectory, "api"));
            File.Copy(dbSourcePath, Path.Combine(artifactDirectory, "db"));

            var apiServiceId = Guid.CreateVersion7();
            var dbServiceId = Guid.CreateVersion7();
            const string guidTemplate = "${PORT@11111111-1111-7111-8111-111111111111}";
            var yaml = $$"""
                serviceScope: document
                services:
                  api:
                    source:
                      path: {{apiSourcePath}}
                    args: ['${PORT@db}', '{{guidTemplate}}']
                    env:
                      DB_PORT: '${PORT@db}'
                      GUID_PORT: '{{guidTemplate}}'
                  db:
                    source:
                      path: {{dbSourcePath}}
                """;

            LockSource CreateLockSource(string path)
            {
                var bytes = File.ReadAllBytes(path);
                return new LockSource
                {
                    Kind = "path",
                    Path = Path.GetFullPath(path),
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    Size = bytes.LongLength,
                    FetchedAt = DateTimeOffset.UtcNow
                };
            }

            var settings = new SvchostSettings(
                null,
                new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()),
                new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal)
                {
                    ["demo"] = new SvchostConfigSettings(
                        yaml,
                        new LockModel
                        {
                            Services = new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal)
                            {
                                ["api"] = new LockServiceEntry(CreateLockSource(apiSourcePath), apiServiceId),
                                ["db"] = new LockServiceEntry(CreateLockSource(dbSourcePath), dbServiceId)
                            }
                        })
                });
            var full = new FakeFullConfigurationApi(CreateSnapshot());
            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(settings));
            var reconciler = CreateReconciler(configurationApi, full, dataDirectory);

            var firstReport = await reconciler.ReconcileAsync("test");

            Assert.True(firstReport.Succeeded);
            var firstApi = full.Snapshot.Services.Single(service => service.Id == apiServiceId);
            var expectedDbTemplate = $"${{PORT@{dbServiceId}}}";
            Assert.Equal(expectedDbTemplate, firstApi.ArgumentList[0]);
            Assert.Equal(guidTemplate, firstApi.ArgumentList[1]);
            Assert.Equal(expectedDbTemplate, firstApi.Environment["DB_PORT"]);
            Assert.Equal(guidTemplate, firstApi.Environment["GUID_PORT"]);

            var secondReport = await reconciler.ReconcileAsync("test");

            Assert.True(secondReport.Succeeded);
            var secondApi = full.Snapshot.Services.Single(service => service.Id == apiServiceId);
            Assert.Equal(apiServiceId, secondApi.Id);
            Assert.Equal(expectedDbTemplate, secondApi.ArgumentList[0]);
            Assert.Equal(expectedDbTemplate, secondApi.Environment["DB_PORT"]);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Reconcile_resolves_cross_file_templates_and_scope_directories()
    {
        var root = CreateTempDirectory();
        try
        {
            var globalWorkerSourcePath = Path.Combine(root, "global-worker.bin");
            var globalSharedSourcePath = Path.Combine(root, "global-shared.bin");
            var globalOnlySourcePath = Path.Combine(root, "global-only.bin");
            var documentSharedSourcePath = Path.Combine(root, "document-shared.bin");
            var documentAppSourcePath = Path.Combine(root, "document-app.bin");
            foreach (var sourcePath in new[]
                     {
                         globalWorkerSourcePath,
                         globalSharedSourcePath,
                         globalOnlySourcePath,
                         documentSharedSourcePath,
                         documentAppSourcePath
                     })
            {
                await File.WriteAllTextAsync(sourcePath, Path.GetFileName(sourcePath));
            }

            var dataDirectory = Path.Combine(root, "data");
            var settings = new SvchostSettings(
                null,
                new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()),
                new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal)
                {
                    ["a-global-client"] = new SvchostConfigSettings($$"""
                        serviceScope: global
                        services:
                          worker:
                            source:
                              path: {{globalWorkerSourcePath}}
                            args: ['${PORT@shared}']
                        """),
                    ["z-global-target"] = new SvchostConfigSettings($$"""
                        serviceScope: global
                        services:
                          shared:
                            source:
                              path: {{globalSharedSourcePath}}
                          global-only:
                            source:
                              path: {{globalOnlySourcePath}}
                        """),
                    ["document"] = new SvchostConfigSettings($$"""
                        serviceScope: document
                        services:
                          shared:
                            source:
                              path: {{documentSharedSourcePath}}
                          app:
                            source:
                              path: {{documentAppSourcePath}}
                            args: ['${PORT@shared}', '${PORT@global-only}']
                        """)
                });
            var full = new FakeFullConfigurationApi(CreateSnapshot());
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(settings)),
                full,
                dataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            var globalRoot = Path.GetFullPath(Path.Combine(dataDirectory, "svchost", "global"));
            var documentRoot = Path.GetFullPath(Path.Combine(dataDirectory, "svchost", "document"));
            var globalWorker = full.Snapshot.Services.Single(service =>
                service.FileName == Path.Combine(globalRoot, "artifacts", "worker"));
            var globalShared = full.Snapshot.Services.Single(service =>
                service.FileName == Path.Combine(globalRoot, "artifacts", "shared"));
            var globalOnly = full.Snapshot.Services.Single(service =>
                service.FileName == Path.Combine(globalRoot, "artifacts", "global-only"));
            var documentShared = full.Snapshot.Services.Single(service =>
                service.FileName == Path.Combine(documentRoot, "artifacts", "shared"));
            var documentApp = full.Snapshot.Services.Single(service =>
                service.FileName == Path.Combine(documentRoot, "artifacts", "app"));

            Assert.Equal(globalRoot, globalWorker.WorkingDirectory);
            Assert.Equal(globalRoot, globalShared.WorkingDirectory);
            Assert.Equal(documentRoot, documentShared.WorkingDirectory);
            Assert.Equal(documentRoot, documentApp.WorkingDirectory);
            Assert.True(File.Exists(globalWorker.FileName));
            Assert.True(File.Exists(globalShared.FileName));
            Assert.True(File.Exists(globalOnly.FileName));
            Assert.True(File.Exists(documentShared.FileName));
            Assert.True(File.Exists(documentApp.FileName));
            Assert.True(Directory.Exists(Path.Combine(globalRoot, "tmp")));
            Assert.True(Directory.Exists(Path.Combine(documentRoot, "tmp")));
            Assert.Equal($"${{PORT@{globalShared.Id}}}", globalWorker.ArgumentList.Single());
            Assert.Equal($"${{PORT@{documentShared.Id}}}", documentApp.ArgumentList[0]);
            Assert.Equal($"${{PORT@{globalOnly.Id}}}", documentApp.ArgumentList[1]);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Reconcile_duplicate_global_service_fails_later_config_without_clobbering_owner()
    {
        var root = CreateTempDirectory();
        try
        {
            var alphaSourcePath = Path.Combine(root, "alpha-api.bin");
            var zuluSourcePath = Path.Combine(root, "zulu-api.bin");
            var workerSourcePath = Path.Combine(root, "worker.bin");
            await File.WriteAllTextAsync(alphaSourcePath, "alpha-service");
            await File.WriteAllTextAsync(zuluSourcePath, "zulu-service");
            await File.WriteAllTextAsync(workerSourcePath, "zulu-worker");

            var dataDirectory = Path.Combine(root, "data");
            var globalRoot = Path.Combine(dataDirectory, "svchost", "global");
            var artifactPath = Path.Combine(globalRoot, "artifacts", "api");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            await File.WriteAllTextAsync(artifactPath, "existing-owner-artifact");

            var serviceId = Guid.CreateVersion7();
            var routeId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var existingService = CreateService(serviceId, true, artifactPath, globalRoot, now);
            var existingRoute = CreateRoute(
                routeId,
                serviceId,
                "/api",
                "{\"owner\":\"nekostick.svchost\",\"config\":\"alpha\",\"service\":\"api\"}");
            var full = new FakeFullConfigurationApi(CreateSnapshot(
                ImmutableArray.Create(existingService),
                ImmutableArray.Create(existingRoute)));
            var settings = new SvchostSettings(
                null,
                new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()),
                new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal)
                {
                    ["zulu"] = new SvchostConfigSettings($$"""
                        serviceScope: global
                        services:
                          api:
                            source:
                              path: {{zuluSourcePath}}
                          worker:
                            source:
                              path: {{workerSourcePath}}
                        """),
                    ["alpha"] = new SvchostConfigSettings($$"""
                        serviceScope: global
                        services:
                          api:
                            source:
                              path: {{alphaSourcePath}}
                        """)
                });
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(settings)),
                full,
                dataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.False(report.Succeeded);
            Assert.Equal(SyncErrorCode.ReconcileFailed, report.FailureCode);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Contains(full.Snapshot.Services, service => service.Id == serviceId);
            Assert.Contains(full.Snapshot.Routes, route => route.Id == routeId);
            Assert.Equal("alpha-service", await File.ReadAllTextAsync(artifactPath));
            Assert.False(File.Exists(Path.Combine(globalRoot, "artifacts", "worker")));

            Assert.True(Assert.Single(report.Services, service => service.ConfigName == "alpha").Succeeded);
            var failedConfigServices = report.Services
                .Where(service => service.ConfigName == "zulu")
                .OrderBy(service => service.ServiceName, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "api", "worker" }, failedConfigServices.Select(service => service.ServiceName));
            Assert.All(failedConfigServices, service =>
            {
                Assert.False(service.Succeeded);
                Assert.Equal(ServiceDecision.Skipped, service.Decision);
                Assert.Equal(SyncErrorCode.ReconcileFailed, service.FailureCode);
                Assert.Contains("api", service.Error!, StringComparison.Ordinal);
                Assert.Contains("alpha", service.Error!, StringComparison.Ordinal);
            });
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Reconcile_unresolvable_template_target_fails_only_its_service()
    {
        var root = CreateTempDirectory();
        try
        {
            var apiSourcePath = Path.Combine(root, "api.bin");
            var workerSourcePath = Path.Combine(root, "worker.bin");
            await File.WriteAllTextAsync(apiSourcePath, "api");
            await File.WriteAllTextAsync(workerSourcePath, "worker");
            var dataDirectory = Path.Combine(root, "data");
            var settings = new SvchostSettings(
                null,
                new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()),
                new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal)
                {
                    ["demo"] = new SvchostConfigSettings($$"""
                        serviceScope: document
                        services:
                          api:
                            source:
                              path: {{apiSourcePath}}
                            args: ['${PORT@missing}']
                          worker:
                            source:
                              path: {{workerSourcePath}}
                        """)
                });
            var full = new FakeFullConfigurationApi(CreateSnapshot());
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(settings)),
                full,
                dataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.False(report.Succeeded);
            var apiReport = Assert.Single(report.Services, service => service.ServiceName == "api");
            Assert.False(apiReport.Succeeded);
            Assert.Equal(SyncErrorCode.ReconcileFailed, apiReport.FailureCode);
            Assert.Contains("missing", apiReport.Error!, StringComparison.Ordinal);
            Assert.True(Assert.Single(report.Services, service => service.ServiceName == "worker").Succeeded);
            Assert.Contains(full.Snapshot.Services, service =>
                service.FileName == Path.Combine(dataDirectory, "svchost", "demo", "artifacts", "worker"));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }
}
