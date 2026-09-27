using System.Reflection;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed partial class ReconcilerTests
{
    [Fact]
    public async Task Lock_reset_does_not_null_a_persisted_entry_for_a_different_service_id()
    {
        var expectedServiceId = Guid.CreateVersion7();
        var persistedServiceId = Guid.CreateVersion7();
        var entry = await RunGuardedResetAsync(
            persistedServiceId,
            new string('a', 64),
            expectedServiceId,
            new string('a', 64));

        Assert.NotNull(entry.Source);
        Assert.Equal(persistedServiceId, entry.ServiceId);
    }

    [Fact]
    public async Task Lock_reset_does_not_null_a_persisted_entry_for_a_mismatched_sha256()
    {
        var serviceId = Guid.CreateVersion7();
        var entry = await RunGuardedResetAsync(
            serviceId,
            new string('b', 64),
            serviceId,
            new string('a', 64));

        Assert.NotNull(entry.Source);
        Assert.Equal(new string('b', 64), entry.Source!.Sha256);
    }

    [Fact]
    public async Task Lock_reset_nulls_a_matching_entry_and_normalizes_null_sha256()
    {
        var serviceId = Guid.CreateVersion7();
        var entry = await RunGuardedResetAsync(
            serviceId,
            null,
            serviceId,
            string.Empty);

        Assert.Null(entry.Source);
    }

    private static async Task<LockServiceEntry> RunGuardedResetAsync(
        Guid persistedServiceId,
        string? persistedSha256,
        Guid expectedServiceId,
        string expectedSha256)
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "api.bin");
            await File.WriteAllTextAsync(sourcePath, "api-v1");
            var settings = CreatePathSettings(sourcePath, persistedServiceId);
            var persistedEntry = settings.Configs["demo"]!.Lock!.Services["api"];
            persistedEntry.ServiceId = persistedServiceId;
            persistedEntry.Source.Sha256 = persistedSha256!;

            var configurationApi = new FakeConfigurationApi(ToExtensionSettings(settings));
            var settingsStore = new SettingsStore(configurationApi);
            var reconciler = CreateReconciler(
                configurationApi,
                new FakeFullConfigurationApi(CreateSnapshot()),
                root);
            var expectedGenerations = new Dictionary<
                (string ConfigName, string ServiceName),
                (Guid ServiceId, string Sha256)>
            {
                [("demo", "api")] = (expectedServiceId, expectedSha256)
            };

            var method = typeof(Reconciler).GetMethod(
                "ResetAffectedLockSourcesBestEffortAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var invocation = method!.Invoke(
                reconciler,
                new object?[] { expectedGenerations, CancellationToken.None });
            await (ValueTask)invocation!;

            var read = await settingsStore.ReadSettingsAsync();
            Assert.True(read.IsSuccess);
            return read.Value!.Settings!.Configs["demo"]!.Lock!.Services["api"];
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }
}
