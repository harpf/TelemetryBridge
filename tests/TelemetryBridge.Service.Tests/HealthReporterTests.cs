using System.Text.Json;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Health;
using static TelemetryBridge.Service.Tests.TestSupport;

namespace TelemetryBridge.Service.Tests;

public sealed class HealthReporterTests
{
    [Fact]
    public void Snapshot_counts_pending_items()
    {
        var dir = NewTempDir();
        var buffer = new BufferService();
        buffer.Enqueue(new JobEvent { JobName = "a" }, dir);
        buffer.Enqueue(new JobEvent { JobName = "b" }, dir);

        var reporter = new HealthReporter(buffer, Options(bufferPath: dir));
        var status = reporter.Snapshot();

        Assert.Equal(2, status.Pending);
        Assert.Equal(0, status.Retrying);
        Assert.Equal(0, status.DeadLetter);
        Assert.True(status.TotalBytes > 0);
    }

    [Fact]
    public void ToHealthJson_emits_ok_status_and_buffer_counts()
    {
        var dir = NewTempDir();
        var buffer = new BufferService();
        buffer.Enqueue(new JobEvent { JobName = "a" }, dir);

        var reporter = new HealthReporter(buffer, Options(bufferPath: dir));
        var json = reporter.ToHealthJson(reporter.Snapshot());

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("buffer").GetProperty("pending").GetInt32());
    }
}
