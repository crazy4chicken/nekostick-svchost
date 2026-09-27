using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Nekostick.ServiceHost.Api;
using Nekostick.ServiceHost.Sync;
using Xunit;

namespace Nekostick.ServiceHost.UnitTests;

public sealed class SyncReportPayloadTests
{
    [Fact]
    public void Sync_report_payload_projects_warnings_node_local_and_notes()
    {
        var service = new ServiceSyncReport(
            "demo",
            "api",
            false,
            false,
            Guid.CreateVersion7(),
            ImmutableArray<Guid>.Empty,
            "The source could not be resolved.")
        {
            Warnings = ImmutableArray.Create("source is not pinned"),
            NodeLocal = true
        };
        var report = new SyncReport(
            false,
            true,
            DateTimeOffset.UtcNow,
            ImmutableArray.Create(service),
            null,
            "Synchronization failed")
        {
            Notes = ImmutableArray.Create("retained deployed service")
        };

        var method = typeof(SvchostApiHandler).GetMethod(
            "SyncReportPayload",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var payload = method!.Invoke(null, new object?[] { report, "demo" });
        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var servicePayload = Assert.Single(root.GetProperty("services").EnumerateArray());

        Assert.Equal(
            "source is not pinned",
            Assert.Single(servicePayload.GetProperty("warnings").EnumerateArray()).GetString());
        Assert.True(servicePayload.GetProperty("nodeLocal").GetBoolean());
        Assert.Equal(
            "retained deployed service",
            Assert.Single(root.GetProperty("notes").EnumerateArray()).GetString());
    }
}
