using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekostick.ServiceHost.Api;
using Nekostick.ServiceHost.Compose;
using Nekostick.ServiceHost.Settings;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class ServiceStatusApiTests
{
    private const string TestApiKey = "service-status-api-key-123456";

    [Fact]
    public async Task Get_counts_consecutive_failed_lifecycle_observations()
    {
        using var fixture = await CreateFixtureAsync();
        var serviceId = await AddServiceConfigAsync(fixture);
        fixture.Supervisor.ServiceId = serviceId;
        fixture.Supervisor.LifecycleState = ExtensionServiceLifecycleState.Failed;

        using var firstDocument = await GetServicesAsync(fixture.Handler);
        Assert.Equal(1, GetSingleService(firstDocument).GetProperty("consecutiveFailures").GetInt32());
        using var secondDocument = await GetServicesAsync(fixture.Handler);
        var service = GetSingleService(secondDocument);
        Assert.Equal(2, service.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal(JsonValueKind.Null, service.GetProperty("lastReconcile").ValueKind);

        fixture.Supervisor.ServiceId = null;
        using var missingRuntimeDocument = await GetServicesAsync(fixture.Handler);
        Assert.Equal(0, GetSingleService(missingRuntimeDocument).GetProperty("consecutiveFailures").GetInt32());

        fixture.Supervisor.ServiceId = serviceId;
        fixture.Supervisor.LifecycleState = ExtensionServiceLifecycleState.Failed;
        using var resumedDocument = await GetServicesAsync(fixture.Handler);
        Assert.Equal(1, GetSingleService(resumedDocument).GetProperty("consecutiveFailures").GetInt32());

        fixture.Supervisor.LifecycleState = ExtensionServiceLifecycleState.Running;
        using var resetDocument = await GetServicesAsync(fixture.Handler);
        Assert.Equal(0, GetSingleService(resetDocument).GetProperty("consecutiveFailures").GetInt32());
    }

    [Fact]
    public async Task Get_projects_last_reconcile_and_marks_drift_by_trigger()
    {
        using var fixture = await CreateFixtureAsync();
        var serviceId = await AddServiceConfigAsync(fixture);
        var completedAt = DateTimeOffset.UtcNow;
        fixture.Handler.RecordReport(CreateReport(serviceId, "startup", completedAt));

        using var startupDocument = await GetServicesAsync(fixture.Handler);
        var startupService = GetSingleService(startupDocument);
        var startupReport = startupService.GetProperty("lastReconcile");
        Assert.Equal(completedAt, startupReport.GetProperty("completedAt").GetDateTimeOffset());
        Assert.True(startupReport.GetProperty("succeeded").GetBoolean());
        Assert.Equal("startup", startupReport.GetProperty("trigger").GetString());
        Assert.Equal("updated", startupReport.GetProperty("decision").GetString());
        var diff = Assert.Single(startupReport.GetProperty("diffs").EnumerateArray());
        Assert.Equal("image", diff.GetProperty("field").GetString());
        Assert.Equal("v1", diff.GetProperty("oldValue").GetString());
        Assert.Equal("v2", diff.GetProperty("newValue").GetString());
        Assert.True(startupService.GetProperty("driftCorrected").GetBoolean());

        fixture.Handler.RecordReport(CreateReport(serviceId, "api-service-start", completedAt.AddSeconds(1)));
        using var apiDocument = await GetServicesAsync(fixture.Handler);
        var apiService = GetSingleService(apiDocument);
        Assert.Equal(
            "api-service-start",
            apiService.GetProperty("lastReconcile").GetProperty("trigger").GetString());
        Assert.False(apiService.GetProperty("driftCorrected").GetBoolean());
    }

    [Fact]
    public async Task Get_projects_run_level_failure_without_a_service_entry()
    {
        using var fixture = await CreateFixtureAsync();
        await AddServiceConfigAsync(fixture);
        var completedAt = DateTimeOffset.UtcNow;
        const string error = "The reconciliation run failed before reporting services.";
        fixture.Handler.RecordReport(new SyncReport(
            false,
            true,
            completedAt,
            ImmutableArray<ServiceSyncReport>.Empty,
            null,
            error)
        {
            FailureCode = SyncErrorCode.ReconcileFailed,
            Trigger = "startup"
        });

        using var document = await GetServicesAsync(fixture.Handler);
        var service = GetSingleService(document);
        var lastReconcile = service.GetProperty("lastReconcile");
        Assert.Equal(completedAt, lastReconcile.GetProperty("completedAt").GetDateTimeOffset());
        Assert.False(lastReconcile.GetProperty("succeeded").GetBoolean());
        Assert.Equal("startup", lastReconcile.GetProperty("trigger").GetString());
        Assert.Equal(JsonValueKind.Null, lastReconcile.GetProperty("decision").ValueKind);
        Assert.Empty(lastReconcile.GetProperty("diffs").EnumerateArray());
        Assert.Equal(error, lastReconcile.GetProperty("error").GetString());
        Assert.Equal("reconcile", lastReconcile.GetProperty("errorKind").GetString());
    }

    private static async Task<Guid> AddServiceConfigAsync(ServiceStatusFixture fixture)
    {
        var serviceId = Guid.CreateVersion7();
        var lockModel = new LockModel();
        lockModel.Services["api"] = new LockServiceEntry(new LockSource(), serviceId);
        var result = await fixture.SettingsStore.UpdateSettingsAsync(settings =>
        {
            settings.Configs["demo"] = new SvchostConfigSettings("services: {}", lockModel);
            return settings;
        });
        Assert.True(result.IsSuccess);
        return serviceId;
    }

    private static SyncReport CreateReport(Guid serviceId, string trigger, DateTimeOffset completedAt) =>
        new(
            true,
            true,
            completedAt,
            ImmutableArray.Create(new ServiceSyncReport(
                "demo",
                "api",
                true,
                true,
                serviceId,
                ImmutableArray<Guid>.Empty,
                null)
            {
                Decision = ServiceDecision.Updated,
                Diffs = ImmutableArray.Create(new ServiceFieldDiff("image", "v1", "v2"))
            }),
            null,
            null)
        {
            Trigger = trigger
        };

    private static async Task<JsonDocument> GetServicesAsync(SvchostApiHandler handler)
    {
        var response = await handler.HandleStreamingAsync(
            new ExtensionStreamingRequest(
                "GET",
                "/svchost/api/services",
                new[]
                {
                    new KeyValuePair<string, IEnumerable<string>>(
                        "X-Api-Key",
                        new[] { TestApiKey })
                },
                new MemoryStream(Array.Empty<byte>(), writable: false)),
            CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        return await JsonDocument.ParseAsync(response.BodyStream);
    }

    private static JsonElement GetSingleService(JsonDocument document) =>
        Assert.Single(document.RootElement.GetProperty("services").EnumerateArray());

    private static async Task<ServiceStatusFixture> CreateFixtureAsync()
    {
        var configurationApi = new FakeConfigurationApi();
        var settingsStore = new SettingsStore(configurationApi);
        var supervisorApi = EmptySupervisorProxy.Create(out var supervisor);
        var initializationFullConfiguration = new FakeFullConfigurationApi(CreateHostConfigurationSnapshot());
        var bridge = new FakeBridge { SupervisorApi = supervisorApi, FullConfiguration = initializationFullConfiguration };
        var apiKeyService = new ApiKeyService(settingsStore, bridge);
        var initialization = await apiKeyService.InitializeAsync();
        Assert.True(initialization.Succeeded);
        var keyWrite = await apiKeyService.SetPermanentKeyAsync(TestApiKey);
        Assert.True(keyWrite.IsSuccess);

        var composeParser = new ComposeFileParser();
        var reconciler = new Reconciler(
            settingsStore,
            composeParser,
            new SourceResolver(),
            new FakeFullConfigurationApi(CreateHostConfigurationSnapshot()));
        var handler = new SvchostApiHandler(
            apiKeyService,
            settingsStore,
            composeParser,
            reconciler,
            bridge);
        return new ServiceStatusFixture(handler, settingsStore, supervisor);
    }

    private static HostConfigurationSnapshot CreateHostConfigurationSnapshot() =>
        new(
            0,
            new GlobalSettingsConfiguration(),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

    private sealed class ServiceStatusFixture : IDisposable
    {
        public ServiceStatusFixture(
            SvchostApiHandler handler,
            SettingsStore settingsStore,
            EmptySupervisorProxy supervisor)
        {
            Handler = handler;
            SettingsStore = settingsStore;
            Supervisor = supervisor;
        }

        public SvchostApiHandler Handler { get; }

        public SettingsStore SettingsStore { get; }

        public EmptySupervisorProxy Supervisor { get; }

        public void Dispose() => Handler.Dispose();
    }

    public class EmptySupervisorProxy : DispatchProxy
    {
        public Guid? ServiceId { get; set; }

        public ExtensionServiceLifecycleState LifecycleState { get; set; }

        public static IExtensionSupervisorApi Create(out EmptySupervisorProxy fake)
        {
            var supervisor = DispatchProxy.Create<IExtensionSupervisorApi, EmptySupervisorProxy>();
            fake = (EmptySupervisorProxy)(object)supervisor;
            return supervisor;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is { Name: nameof(IExtensionSupervisorApi.ReadAsync) } readMethod)
            {
                return CreateReadResult(readMethod.ReturnType);
            }

            throw new NotSupportedException($"Unexpected supervisor call: {targetMethod?.Name}.");
        }

        private object CreateReadResult(Type returnType)
        {
            var resultType = returnType.GetGenericArguments().Single();
            var valueType = resultType.GetProperty("Value")!.PropertyType;
            var success = resultType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method =>
                {
                    var parameters = method.GetParameters();
                    return method.Name == "Success" &&
                        parameters.Length == 1 &&
                        parameters[0].ParameterType == valueType;
                });
            var value = ServiceId is { } serviceId
                ? CreateSnapshotCollection(valueType, serviceId, LifecycleState)
                : CreateEmptyCollection(valueType);
            var result = success.Invoke(null, new[] { value })!;

            if (returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                return Activator.CreateInstance(returnType, result)!;
            }

            if (returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, new[] { result })!;
            }

            throw new NotSupportedException($"Unexpected read result type: {returnType}.");
        }

        private static object CreateSnapshotCollection(
            Type collectionType,
            Guid serviceId,
            ExtensionServiceLifecycleState lifecycleState)
        {
            var elementType = collectionType.IsArray
                ? collectionType.GetElementType()!
                : collectionType.GetGenericArguments().Single();
            var snapshot = CreateRuntimeSnapshot(elementType, serviceId, lifecycleState);

            if (collectionType.IsArray)
            {
                var array = Array.CreateInstance(elementType, 1);
                array.SetValue(snapshot, 0);
                return array;
            }

            if (collectionType.IsGenericType &&
                collectionType.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
            {
                var create = typeof(ImmutableArray)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(method =>
                    {
                        var parameters = method.GetParameters();
                        return method.Name == nameof(ImmutableArray.Create) &&
                            method.IsGenericMethodDefinition &&
                            parameters.Length == 1 &&
                            parameters[0].ParameterType.IsGenericParameter;
                    });
                return create.MakeGenericMethod(elementType).Invoke(null, new[] { snapshot })!;
            }

            var values = Array.CreateInstance(elementType, 1);
            values.SetValue(snapshot, 0);
            if (collectionType.IsInstanceOfType(values))
            {
                return values;
            }

            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = Activator.CreateInstance(listType)!;
            listType.GetMethod("Add")!.Invoke(list, new[] { snapshot });
            return list;
        }

        private static object CreateRuntimeSnapshot(
            Type snapshotType,
            Guid serviceId,
            ExtensionServiceLifecycleState lifecycleState)
        {
            var snapshot = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(snapshotType);
            SetSnapshotProperty(snapshot, nameof(ExtensionServiceRuntimeSnapshot.ServiceId), serviceId);
            SetSnapshotProperty(snapshot, nameof(ExtensionServiceRuntimeSnapshot.LifecycleState), lifecycleState);
            return snapshot;
        }

        private static void SetSnapshotProperty(object snapshot, string propertyName, object value)
        {
            var property = snapshot.GetType().GetProperty(propertyName)!;
            var setter = property.GetSetMethod(nonPublic: true);
            if (setter is not null)
            {
                setter.Invoke(snapshot, new[] { value });
                return;
            }

            var backingField = snapshot.GetType().GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            backingField!.SetValue(snapshot, value);
        }

        private static object CreateEmptyCollection(Type collectionType)
        {
            if (collectionType.IsGenericType &&
                collectionType.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
            {
                var elementType = collectionType.GetGenericArguments()[0];
                var create = typeof(ImmutableArray)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(method =>
                        method.Name == nameof(ImmutableArray.Create) &&
                        method.IsGenericMethodDefinition &&
                        method.GetParameters().Length == 0);
                return create.MakeGenericMethod(elementType).Invoke(null, null)!;
            }

            if (collectionType.IsArray)
            {
                return Array.CreateInstance(collectionType.GetElementType()!, 0);
            }

            var enumerableType = new[] { collectionType }
                .Concat(collectionType.GetInterfaces())
                .First(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return Array.CreateInstance(enumerableType.GetGenericArguments()[0], 0);
        }
    }
}
