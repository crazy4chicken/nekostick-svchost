using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed partial class ReconcilerTests
{

    private static Reconciler CreateReconciler(
        FakeConfigurationApi configurationApi,
        FakeFullConfigurationApi fullConfiguration,
        string dataDirectory,
        IExtensionSupervisorApi? supervisor = null)
    {
        configurationApi.SettingsWritten = fullConfiguration.RecordSettingsWrite;
        var currentSettings = configurationApi.CurrentSettings;
        if (currentSettings is not null)
        {
            var snapshot = fullConfiguration.Snapshot;
            if (!snapshot.ExtensionSettings.Any(entry =>
                    string.Equals(entry.ExtensionId, currentSettings.ExtensionId, StringComparison.Ordinal)))
            {
                fullConfiguration.Snapshot = new HostConfigurationSnapshot(
                    snapshot.Version,
                    snapshot.GlobalSettings,
                    snapshot.Routes,
                    snapshot.Services,
                    snapshot.ExtensionRecords,
                    snapshot.ExtensionSettings.Add(currentSettings));
            }

            fullConfiguration.SettingsCommitted = committed =>
            {
                if (string.Equals(
                        committed.ExtensionId,
                        SvchostSettingsSchema.ExtensionId,
                        StringComparison.Ordinal))
                {
                    configurationApi.UpdateSettings(committed);
                }
            };
        }

        return new Reconciler(
            new SettingsStore(configurationApi),
            new ComposeFileParser(),
            new SourceResolver(),
            fullConfiguration,
            dataDirectory,
            supervisor);
    }

    private static SvchostSettings CreatePathSettings(
        string sourcePath,
        Guid serviceId,
        string[]? stopped = null,
        string? args = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var content = File.ReadAllBytes(source);
        var lockSource = new LockSource
        {
            Kind = "path",
            Path = source,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            Size = content.LongLength,
            FetchedAt = DateTimeOffset.UtcNow
        };
        var yaml = args is null
            ? $"serviceScope: document\nservices:\n  api:\n    source:\n      path: {source}"
            : $"serviceScope: document\nservices:\n  api:\n    source:\n      path: {source}\n    args: [\"{args}\"]";
        return new SvchostSettings(
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
                            ["api"] = new LockServiceEntry(lockSource, serviceId)
                        }
                    },
                    stopped)
            });
    }

    private static SvchostSettings CreateSettings() =>
        new(null, new SvchostRouteSettings(Guid.CreateVersion7(), Guid.CreateVersion7()));

    private static ExtensionSettingsConfiguration ToExtensionSettings(SvchostSettings settings) =>
        new(
            SvchostSettingsSchema.ExtensionId,
            SvchostSettingsSchema.CurrentVersion,
            JsonSerializer.Serialize(settings),
            1);

    private static string CanonicalizeHostJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new StringBuilder();
        AppendHostJson(document.RootElement, result);
        return result.ToString();
    }

    private static void AppendHostJson(JsonElement value, StringBuilder result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                result.Append('{');
                var firstProperty = true;
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name.Length)
                             .ThenBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        result.Append(", ");
                    }

                    result.Append(JsonSerializer.Serialize(property.Name)).Append(": ");
                    AppendHostJson(property.Value, result);
                    firstProperty = false;
                }

                result.Append('}');
                break;
            case JsonValueKind.Array:
                result.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        result.Append(", ");
                    }

                    AppendHostJson(item, result);
                    firstItem = false;
                }

                result.Append(']');
                break;
            case JsonValueKind.String:
                result.Append(JsonSerializer.Serialize(value.GetString()));
                break;
            default:
                result.Append(value.GetRawText());
                break;
        }
    }

    private static HostConfigurationSnapshot CreateSnapshot(
        ImmutableArray<ServiceConfiguration>? services = null,
        ImmutableArray<RouteConfiguration>? routes = null) =>
        new(
            12,
            new GlobalSettingsConfiguration(),
            routes ?? ImmutableArray<RouteConfiguration>.Empty,
            services ?? ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

    private static ServiceConfiguration CreateService(
        Guid id,
        bool enabled,
        string fileName,
        string workingDirectory,
        DateTimeOffset createdAt,
        ImmutableArray<string>? arguments = null) =>
        new(
            id,
            enabled,
            fileName,
            arguments ?? ImmutableArray<string>.Empty,
            workingDirectory,
            ImmutableDictionary<string, string>.Empty,
            ServiceStartMode.Eager,
            ServiceRestartPolicy.OnFailure,
            new ServiceHealthCheckConfiguration(ServiceHealthCheckType.Process, null, TimeSpan.FromSeconds(5)),
            createdAt,
            createdAt,
            3);

    private static RouteConfiguration CreateRoute(
        Guid id,
        Guid serviceId,
        string prefix,
        string metadataJson) =>
        new(
            id,
            true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Prefix,
                prefix,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            new MicroserviceRouteTargetConfiguration(serviceId),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            metadataJson,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1);

    private static async Task<PathFixture> CreateReusablePathFixtureAsync(bool changedSnapshot = false)
    {
        var root = CreateTempDirectory();
        var sourcePath = Path.Combine(root, "api.bin");
        await File.WriteAllTextAsync(sourcePath, "api-v1");
        var dataDirectory = Path.Combine(root, "data");
        var serviceRoot = Path.Combine(dataDirectory, "svchost", "demo");
        var settings = CreatePathSettings(sourcePath, Guid.CreateVersion7(), args: changedSnapshot ? "--changed" : null);
        var artifactPath = GetContentAddressedArtifactPath(serviceRoot, "api", sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.Copy(sourcePath, artifactPath);
        var serviceId = settings.Configs["demo"]!.Lock.Services["api"].ServiceId;
        var snapshotService = CreateService(
            serviceId,
            true,
            artifactPath,
            serviceRoot,
            DateTimeOffset.UtcNow,
            ImmutableArray<string>.Empty);
        return new PathFixture(root, dataDirectory, sourcePath, settings, CreateSnapshot([snapshotService]));
    }

    private static string GetContentAddressedArtifactPath(string serviceRoot, string serviceName, string sourcePath)
    {
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();
        return Path.Combine(serviceRoot, "artifacts", "sha256", serviceName, digest, serviceName);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "nekostick-svchost-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PathFixture(
        string Root,
        string DataDirectory,
        string SourcePath,
        SvchostSettings Settings,
        HostConfigurationSnapshot Snapshot);
}
