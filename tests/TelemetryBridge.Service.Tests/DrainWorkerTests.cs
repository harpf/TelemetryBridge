using Microsoft.Extensions.Logging.Abstractions;
using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Hosting;
using static TelemetryBridge.Service.Tests.TestSupport;

namespace TelemetryBridge.Service.Tests;

public sealed class DrainWorkerTests
{
    [Fact]
    public async Task Startup_runs_a_drain_pass_immediately()
    {
        var drainer = new FakeDrainer();
        var worker = new DrainWorker(drainer, Options(flushSeconds: 30), NullLogger<DrainWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => drainer.Count >= 1, TimeSpan.FromSeconds(5),
                "the worker should drain once at startup");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(drainer.Count >= 1);
    }

    [Fact]
    public async Task Interval_triggers_repeated_drains()
    {
        var drainer = new FakeDrainer();
        // FlushInterval clamps to 1s minimum, so a couple of ticks happen quickly.
        var worker = new DrainWorker(drainer, Options(flushSeconds: 1), NullLogger<DrainWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => drainer.Count >= 3, TimeSpan.FromSeconds(10),
                "startup drain plus at least two interval ticks should have run");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(drainer.Count >= 3);
    }

    [Fact]
    public async Task StopAsync_runs_a_final_flush()
    {
        var drainer = new FakeDrainer();
        var worker = new DrainWorker(drainer, Options(flushSeconds: 30), NullLogger<DrainWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => drainer.Count >= 1, TimeSpan.FromSeconds(5),
            "startup drain should complete before shutdown");

        var before = drainer.Count;
        await worker.StopAsync(CancellationToken.None);

        Assert.True(drainer.Count > before,
            "graceful shutdown should run one final drain to flush in-flight items");
    }

    [Fact]
    public async Task A_failing_drain_pass_does_not_crash_the_worker()
    {
        var drainer = new FakeDrainer
        {
            OnDrain = _ => throw new InvalidOperationException("boom")
        };
        var worker = new DrainWorker(drainer, Options(flushSeconds: 1), NullLogger<DrainWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Even though every pass throws, the worker keeps ticking.
            await WaitForAsync(() => drainer.Count >= 2, TimeSpan.FromSeconds(8),
                "the worker should survive a throwing drain pass and keep retrying");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(drainer.Count >= 2);
    }
}
