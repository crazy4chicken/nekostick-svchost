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

            var report = await reconciler.ReconcileAsync();

            Assert.True(report.Succeeded);
            Assert.Equal(1, full.ReplaceCallCount);
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

            var firstReport = await reconciler.ReconcileAsync();

            Assert.True(firstReport.Succeeded);
            var firstApi = full.Snapshot.Services.Single(service => service.Id == apiServiceId);
            var expectedDbTemplate = $"${{PORT@{dbServiceId}}}";
            Assert.Equal(expectedDbTemplate, firstApi.ArgumentList[0]);
            Assert.Equal(guidTemplate, firstApi.ArgumentList[1]);
            Assert.Equal(expectedDbTemplate, firstApi.Environment["DB_PORT"]);
            Assert.Equal(guidTemplate, firstApi.Environment["GUID_PORT"]);

            var secondReport = await reconciler.ReconcileAsync();

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
}
