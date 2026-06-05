using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Hosting;
using TelemetryBridge.Service.Telemetry;
using static TelemetryBridge.Service.Tests.TestSupport;

namespace TelemetryBridge.Service.Tests;

public sealed class RetryWorkerDrainerTests
{
    /// <summary>A stand-in exporter so the drainer can run without networking.</summary>
    private sealed class StubExporter : IJobEventExporter
    {
        private readonly ExportResult _result;
        public int Calls { get; private set; }
        public StubExporter(ExportResult result) => _result = result;

        public Task<ExportResult> SendAsync(JobEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task Drain_invokes_the_exporter_and_delivers_buffered_items()
    {
        var options = Options();
        var buffer = new BufferService();
        buffer.Enqueue(new JobEvent { JobName = "nightly", Status = "succeeded" }, options.Bridge.Buffer.Path);

        var exporter = new StubExporter(ExportResult.Success("ok"));
        var drainer = new RetryWorkerDrainer(buffer, exporter, options);

        var result = await drainer.DrainOnceAsync();

        Assert.Equal(1, exporter.Calls);
        Assert.Equal(1, result.Delivered);
        Assert.Empty(buffer.List(options.Bridge.Buffer.Path));
    }

    [Fact]
    public async Task A_failed_export_leaves_the_item_buffered_for_retry()
    {
        var options = Options();
        var buffer = new BufferService();
        buffer.Enqueue(new JobEvent { JobName = "nightly", Status = "succeeded" }, options.Bridge.Buffer.Path);

        var exporter = new StubExporter(ExportResult.Failed("collector down"));
        var drainer = new RetryWorkerDrainer(buffer, exporter, options);

        var result = await drainer.DrainOnceAsync();

        Assert.Equal(1, exporter.Calls);
        Assert.Equal(1, result.Retried);
        Assert.Single(buffer.List(options.Bridge.Buffer.Path)); // still durable
    }
}
