using System.Text.Json;
using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed partial class ReconcilerTests
{
    [Fact]
    public void BuildChangeSet_preserves_unmanaged_values_and_replaces_managed_values()
    {
        var managedServiceId = Guid.CreateVersion7();
        var unmanagedServiceId = Guid.CreateVersion7();
        var managedRouteId = Guid.CreateVersion7();
        var unmanagedRouteId = Guid.CreateVersion7();
        var orphanRouteId = Guid.CreateVersion7();
        var ownerTagOnlyRouteId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var globalSettings = new GlobalSettingsConfiguration();
        var extensionRecords = ImmutableArray.Create(
            new ExtensionRecordConfiguration("other.extension", "Other.Entry", default, now, now, 4));
        var extensionSettings = ImmutableArray.Create(
            new ExtensionSettingsConfiguration("other.extension", 1, "{\"value\":true}", 8));
        var oldManagedService = CreateService(managedServiceId, true, "/old/managed", "/old", now);
        var unmanagedService = CreateService(unmanagedServiceId, true, "/unmanaged", "/unmanaged", now);
        var oldManagedRoute = CreateRoute(managedRouteId, managedServiceId, "/old", "{}");
        var unmanagedRoute = CreateRoute(unmanagedRouteId, unmanagedServiceId, "/unmanaged", "{}");
        var orphanRoute = CreateRoute(
            orphanRouteId,
            unmanagedServiceId,
            "/orphan",
            "{\"owner\":\"nekostick.svchost\",\"config\":\"missing\",\"service\":\"api\"}");
        var ownerTagOnlyRoute = CreateRoute(
            ownerTagOnlyRouteId,
            unmanagedServiceId,
            "/owner-tag-only",
            "{\"owner\":\"nekostick.svchost\"}");
        var snapshot = new HostConfigurationSnapshot(
            12,
            globalSettings,
            [oldManagedRoute, unmanagedRoute, orphanRoute, ownerTagOnlyRoute],
            [oldManagedService, unmanagedService],
            extensionRecords,
            extensionSettings);
        var desiredService = CreateService(managedServiceId, false, "/new/managed", "/new", now);
        var desiredRoute = CreateRoute(managedRouteId, managedServiceId, "/new", "{\"owner\":\"nekostick.svchost\",\"config\":\"demo\",\"service\":\"api\"}");
        var reconciler = CreateReconciler(new FakeConfigurationApi(), new FakeFullConfigurationApi(snapshot), Path.GetTempPath());

        var changes = reconciler.BuildChangeSet(
            snapshot,
            [managedServiceId],
            [managedRouteId],
            [desiredService],
            [desiredRoute]);

        Assert.Same(globalSettings, changes.GlobalSettings);
        Assert.Equal(extensionRecords, changes.ExtensionRecords);
        Assert.Equal(extensionSettings, changes.ExtensionSettings);
        Assert.Equal([unmanagedServiceId, managedServiceId], changes.Services.Select(service => service.Id));
        Assert.Equal(
            [unmanagedRouteId, ownerTagOnlyRouteId, managedRouteId],
            changes.Routes.Select(route => route.Id));
        Assert.DoesNotContain(changes.Routes, route => route.Id == orphanRouteId);
        Assert.Equal("/new/managed", changes.Services.Single(service => service.Id == managedServiceId).FileName);
    }

    [Fact]
    public async Task Reconcile_route_matcher_order_and_duplicates_skip_replace()
    {
        var fixture = await CreateRouteReconcileFixtureAsync(
            "current",
            "current",
            snapshotHosts: ImmutableArray.Create("admin.example.com", "api.example.com", "api.example.com"),
            snapshotMethods: ImmutableArray.Create("POST", "GET", "GET"));
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(fixture.Settings)),
                full,
                fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Null(report.WrittenConfigurationVersion);
            Assert.Equal(0, full.ReplaceCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_removed_service_cascades_route_removal_before_service_removal()
    {
        var root = CreateTempDirectory();
        try
        {
            var serviceId = Guid.CreateVersion7();
            var routeId = Guid.CreateVersion7();
            var settings = new SvchostSettings(
                null,
                new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()),
                new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal)
                {
                    ["demo"] = new SvchostConfigSettings(
                        "services: {}",
                        new LockModel
                        {
                            Services = new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal)
                            {
                                ["api"] = new LockServiceEntry(new LockSource(), serviceId, [routeId])
                            }
                        })
                });
            var service = CreateService(serviceId, true, "/old/managed", "/old", DateTimeOffset.UtcNow);
            var route = CreateRoute(
                routeId,
                serviceId,
                "/old",
                "{\"owner\":\"nekostick.svchost\",\"config\":\"demo\",\"service\":\"api\"}");
            var full = new FakeFullConfigurationApi(CreateSnapshot([service], [route]));
            var reconciler = CreateReconciler(new FakeConfigurationApi(ToExtensionSettings(settings)), full, root);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Equal(2, full.ReplaceCallCount);
            var firstChanges = full.ChangesHistory[0];
            Assert.Contains(firstChanges.Services, item => item.Id == serviceId);
            Assert.DoesNotContain(firstChanges.Routes, item => item.Id == routeId);
            Assert.Empty(full.Snapshot.Services);
            Assert.Empty(full.Snapshot.Routes);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Reconcile_removed_service_fails_loudly_when_foreign_route_targets_it()
    {
        var root = CreateTempDirectory();
        try
        {
            var serviceId = Guid.CreateVersion7();
            var routeId = Guid.CreateVersion7();
            var settings = new SvchostSettings(
                null,
                new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()),
                new Dictionary<string, SvchostConfigSettings>(StringComparer.Ordinal)
                {
                    ["demo"] = new SvchostConfigSettings(
                        "services: {}",
                        new LockModel
                        {
                            Services = new Dictionary<string, LockServiceEntry>(StringComparer.Ordinal)
                            {
                                ["api"] = new LockServiceEntry(new LockSource(), serviceId)
                            }
                        })
                });
            var service = CreateService(serviceId, true, "/old/managed", "/old", DateTimeOffset.UtcNow);
            var foreignRoute = CreateRoute(routeId, serviceId, "/foreign", "{}");
            var full = new FakeFullConfigurationApi(CreateSnapshot([service], [foreignRoute]));
            var reconciler = CreateReconciler(new FakeConfigurationApi(ToExtensionSettings(settings)), full, root);

            var report = await reconciler.ReconcileAsync("test");

            Assert.False(report.Succeeded);
            Assert.Equal(0, full.ReplaceCallCount);
            Assert.Contains("not managed by svchost", report.Error);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Reconcile_host_patterns_case_only_changes_skip_replace()
    {
        var fixture = await CreateRouteReconcileFixtureAsync(
            "current",
            "current",
            snapshotHosts: ImmutableArray.Create("ADMIN.EXAMPLE.COM", "API.EXAMPLE.COM"));
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(fixture.Settings)),
                full,
                fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Null(report.WrittenConfigurationVersion);
            Assert.Equal(0, full.ReplaceCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_different_host_patterns_replace_once()
    {
        var fixture = await CreateRouteReconcileFixtureAsync(
            "current",
            "current",
            snapshotHosts: ImmutableArray.Create("api.example.com", "other.example.com"));
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(fixture.Settings)),
                full,
                fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(fixture.Snapshot.Version + 1, report.WrittenConfigurationVersion);
            Assert.Equal(
                ["api.example.com", "admin.example.com"],
                full.LastChanges!.Routes.Single().Matcher.HostPatterns.ToArray());
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_different_methods_replace_once()
    {
        var fixture = await CreateRouteReconcileFixtureAsync(
            "current",
            "current",
            snapshotMethods: ImmutableArray.Create("GET", "DELETE"));
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(fixture.Settings)),
                full,
                fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(fixture.Snapshot.Version + 1, report.WrittenConfigurationVersion);
            Assert.Equal(
                ["GET", "POST"],
                full.LastChanges!.Routes.Single().Matcher.Methods.ToArray());
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_metadata_json_property_order_and_whitespace_skip_replace()
    {
        var fixture = await CreateRouteReconcileFixtureAsync(
            "current",
            "current",
            snapshotMetadataJson: """
                {
                  "service": "api",
                  "owner": "nekostick.svchost",
                  "config": "demo"
                }
                """);
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(fixture.Settings)),
                full,
                fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Null(report.WrittenConfigurationVersion);
            Assert.Equal(0, full.ReplaceCallCount);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_changed_service_environment_replaces_once()
    {
        var fixture = await CreateRouteReconcileFixtureAsync("after", "before");
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(fixture.Settings)),
                full,
                fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(fixture.Snapshot.Version, full.LastExpectedVersion);
            Assert.Equal(fixture.Snapshot.Version + 1, report.WrittenConfigurationVersion);
            Assert.Equal("after", full.LastChanges!.Services.Single().Environment["MODE"]);
            Assert.Equal(ServiceDecision.Updated, Assert.Single(report.Services).Decision);
            var environmentDiff = Assert.Single(Assert.Single(report.Services).Diffs);
            Assert.Equal("env.MODE", environmentDiff.Field);
            Assert.Equal("***", environmentDiff.OldValue);
            Assert.Equal("***", environmentDiff.NewValue);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_semantically_different_metadata_json_replaces_once()
    {
        var fixture = await CreateRouteReconcileFixtureAsync(
            "current",
            "current",
            snapshotMetadataJson: """{"owner":"nekostick.svchost","config":"other","service":"api"}""");
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(fixture.Settings)),
                full,
                fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(fixture.Snapshot.Version + 1, report.WrittenConfigurationVersion);
            using var metadataDocument = JsonDocument.Parse(full.LastChanges!.Routes.Single().MetadataJson);
            Assert.Equal("demo", metadataDocument.RootElement.GetProperty("config").GetString());
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Reconcile_rewrites_cleared_route_owner_once()
    {
        var fixture = await CreateRouteReconcileFixtureAsync(
            "current",
            "current",
            snapshotOwnerExtensionId: null);
        try
        {
            var full = new FakeFullConfigurationApi(fixture.Snapshot);
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(fixture.Settings)),
                full,
                fixture.DataDirectory);

            var report = await reconciler.ReconcileAsync("test");

            Assert.True(report.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
            Assert.Equal(
                SvchostSettingsSchema.ExtensionId,
                full.LastChanges!.Routes.Single().OwnerExtensionId);
        }
        finally
        {
            DeleteTempDirectory(fixture.Root);
        }
    }

    private static async Task<PathFixture> CreateRouteReconcileFixtureAsync(
        string desiredEnvironment,
        string snapshotEnvironment,
        string? snapshotMetadataJson = null,
        ImmutableArray<string>? snapshotHosts = null,
        ImmutableArray<string>? snapshotMethods = null,
        string? snapshotOwnerExtensionId = SvchostSettingsSchema.ExtensionId)
    {
        var fixture = await CreateReusablePathFixtureAsync();
        var existingService = fixture.Snapshot.Services.Single();
        var serviceId = existingService.Id;
        var routeId = Guid.CreateVersion7();
        var config = fixture.Settings.Configs["demo"];
        config.Yaml = $$"""
            serviceScope: document
            services:
              api:
                source:
                  path: {{fixture.SourcePath}}
                env:
                  MODE: {{desiredEnvironment}}
                route:
                  prefix: /api
                  methods: [GET, POST]
                  hosts: [api.example.com, admin.example.com]
            """;
        config.Lock.Services["api"].RouteIds = [routeId];

        var snapshotService = new ServiceConfiguration(
            existingService.Id,
            existingService.Enabled,
            existingService.FileName,
            existingService.ArgumentList,
            existingService.WorkingDirectory,
            ImmutableDictionary<string, string>.Empty.Add("MODE", snapshotEnvironment),
            existingService.StartMode,
            existingService.RestartPolicy,
            existingService.HealthCheck,
            existingService.CreatedAt,
            existingService.UpdatedAt,
            existingService.Version);
        var snapshotCreatedAt = DateTimeOffset.UtcNow;
        var snapshotRoute = new RouteConfiguration(
            routeId,
            true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Prefix,
                "/api",
                snapshotHosts ?? ImmutableArray.Create("api.example.com", "admin.example.com"),
                snapshotMethods ?? ImmutableArray.Create("GET", "POST")),
            new MicroserviceRouteTargetConfiguration(serviceId),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            snapshotMetadataJson ?? """{"owner":"nekostick.svchost","config":"demo","service":"api"}""",
            snapshotCreatedAt,
            snapshotCreatedAt,
            1,
            ownerExtensionId: snapshotOwnerExtensionId);
        return fixture with
        {
            Snapshot = new HostConfigurationSnapshot(
                fixture.Snapshot.Version,
                fixture.Snapshot.GlobalSettings,
                [snapshotRoute],
                [snapshotService],
                fixture.Snapshot.ExtensionRecords,
                fixture.Snapshot.ExtensionSettings)
        };
    }

    [Fact]
    public async Task Reconcile_extra_managed_ids_are_removed_from_full_snapshot()
    {
        var root = CreateTempDirectory();
        try
        {
            var serviceId = Guid.CreateVersion7();
            var routeId = Guid.CreateVersion7();
            var snapshot = CreateSnapshot(
                services: [CreateService(serviceId, true, "/old", "/old", DateTimeOffset.UtcNow)],
                routes: [CreateRoute(routeId, serviceId, "/old", "{}")] );
            var full = new FakeFullConfigurationApi(snapshot);
            var reconciler = CreateReconciler(
                new FakeConfigurationApi(ToExtensionSettings(CreateSettings())),
                full,
                root);

            var report = await reconciler.ReconcileAsync([serviceId], [routeId], "test");

            Assert.True(report.Succeeded);
            // Two writes: routes first, then the services once the re-read
            // snapshot shows no route still targeting them.
            Assert.Equal(2, full.ReplaceCallCount);
            var firstChanges = full.ChangesHistory[0];
            Assert.Contains(firstChanges.Services, item => item.Id == serviceId);
            Assert.DoesNotContain(firstChanges.Routes, item => item.Id == routeId);
            Assert.Empty(full.LastChanges!.Services);
            Assert.Empty(full.LastChanges.Routes);
            Assert.Empty(full.Snapshot.Services);
            Assert.Empty(full.Snapshot.Routes);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }
}
