using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

public sealed class RetryWorkerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tb-retry-tests", Guid.NewGuid().ToString("N"));

    private string BufferPath => Path.Combine(_root, "buffer");
    private string DeadLetterPath => Path.Combine(_root, "dead-letter");

    private DateTimeOffset _now = DateTimeOffset.Parse("2026-06-05T10:00:00Z");

    // Zero-jitter, 1s base backoff for predictable next-eligible math in tests.
    private static readonly BackoffPolicy NoJitter =
        new(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), rng: () => 1.0);

    private RetryOptions Options(int maxAttempts = 5, int maxAgeHours = 72, int batchSize = 50) => new()
    {
        BufferPath = BufferPath,
        DeadLetterPath = DeadLetterPath,
        DeadLetterEnabled = true,
        MaxAttempts = maxAttempts,
        MaxEventAgeHours = maxAgeHours,
        BatchSize = batchSize
    };

    private RetryWorker Worker(
        Func<JobEvent, CancellationToken, Task<ExportResult>> exporter,
        RetryOptions options)
        => new(new BufferService(() => _now), exporter, NoJitter, options, () => _now);

    private static Func<JobEvent, CancellationToken, Task<ExportResult>> AlwaysFails =>
        (_, _) => Task.FromResult(ExportResult.Failed("boom"));

    private static Func<JobEvent, CancellationToken, Task<ExportResult>> AlwaysSucceeds =>
        (_, _) => Task.FromResult(ExportResult.Success("ok"));

    [Fact]
    public async Task PendingItem_OnSuccess_IsDeliveredAndDeleted()
    {
        new BufferService(() => _now).Enqueue(new JobEvent { JobName = "ok-job" }, BufferPath);
        var worker = Worker(AlwaysSucceeds, Options());

        var result = await worker.DrainOnceAsync();

        Assert.Equal(1, result.Delivered);
        Assert.Empty(new BufferService().List(BufferPath));
    }

    [Fact]
    public async Task ItemThatFailsThenSucceeds_IsEventuallyDeliveredAndDeleted()
    {
        new BufferService(() => _now).Enqueue(new JobEvent { JobName = "flaky" }, BufferPath);

        var failuresRemaining = 2;
        Func<JobEvent, CancellationToken, Task<ExportResult>> exporter = (_, _) =>
            Task.FromResult(failuresRemaining-- > 0
                ? ExportResult.Failed("transient")
                : ExportResult.Success("ok"));

        var worker = Worker(exporter, Options(maxAttempts: 5));

        // Pass 1: fails -> retrying, next eligible in the future.
        var p1 = await worker.DrainOnceAsync();
        Assert.Equal(1, p1.Retried);
        Assert.Equal(0, p1.Delivered);

        // Not yet eligible: a drain right now does nothing.
        var blocked = await worker.DrainOnceAsync();
        Assert.Equal(0, blocked.Delivered);
        Assert.Equal(0, blocked.Retried);

        // Advance past the backoff window and drain again: fails once more.
        _now = _now.AddMinutes(5);
        var p2 = await worker.DrainOnceAsync();
        Assert.Equal(1, p2.Retried);

        // Advance again: now it succeeds and is removed.
        _now = _now.AddMinutes(5);
        var p3 = await worker.DrainOnceAsync();
        Assert.Equal(1, p3.Delivered);
        Assert.Empty(new BufferService().List(BufferPath));
    }

    [Fact]
    public async Task ItemExceedingMaxAttempts_IsMovedToDeadLetter_AndNoLongerRetried()
    {
        new BufferService(() => _now).Enqueue(new JobEvent { JobName = "doomed" }, BufferPath);
        var worker = Worker(AlwaysFails, Options(maxAttempts: 3));

        // Each pass: one failed attempt, then advance past backoff.
        for (var i = 0; i < 10; i++)
        {
            await worker.DrainOnceAsync();
            _now = _now.AddMinutes(30);
        }

        Assert.Empty(new BufferService().List(BufferPath));
        var dead = new BufferService().List(DeadLetterPath, deadLetter: true);
        var item = Assert.Single(dead);
        Assert.Equal("doomed", item.Event.JobName);
        Assert.Equal(BufferItemState.DeadLetter, item.State);
    }

    [Fact]
    public async Task ItemOlderThanMaxAge_IsDeadLettered_WithoutExporting()
    {
        new BufferService(() => _now).Enqueue(new JobEvent { JobName = "stale" }, BufferPath);

        var exportCalls = 0;
        Func<JobEvent, CancellationToken, Task<ExportResult>> exporter = (_, _) =>
        {
            exportCalls++;
            return Task.FromResult(ExportResult.Failed("boom"));
        };

        var worker = Worker(exporter, Options(maxAgeHours: 1));

        // Jump well past the max age before the first drain.
        _now = _now.AddHours(2);
        var result = await worker.DrainOnceAsync();

        Assert.Equal(0, exportCalls); // too old: never attempted
        Assert.Equal(1, result.DeadLettered);
        Assert.Empty(new BufferService().List(BufferPath));
        Assert.Single(new BufferService().List(DeadLetterPath, deadLetter: true));
    }

    [Fact]
    public async Task DrainOnce_RespectsBatchSize()
    {
        var buffer = new BufferService(() => _now);
        for (var i = 0; i < 5; i++)
        {
            buffer.Enqueue(new JobEvent { JobName = $"job-{i}" }, BufferPath);
            _now = _now.AddSeconds(1); // distinct enqueue timestamps -> stable order
        }

        var exportCalls = 0;
        Func<JobEvent, CancellationToken, Task<ExportResult>> exporter = (_, _) =>
        {
            exportCalls++;
            return Task.FromResult(ExportResult.Failed("boom"));
        };

        var worker = Worker(exporter, Options(batchSize: 2));

        var result = await worker.DrainOnceAsync();

        Assert.Equal(2, exportCalls);
        Assert.Equal(2, result.Retried);

        // 2 now retrying, 3 still pending.
        var remaining = new BufferService().List(BufferPath);
        Assert.Equal(5, remaining.Count);
        Assert.Equal(2, remaining.Count(i => i.State == BufferItemState.Retrying));
        Assert.Equal(3, remaining.Count(i => i.State == BufferItemState.Pending));
    }

    [Fact]
    public async Task DeadLetterDisabled_DropsExhaustedItemInsteadOfParkingIt()
    {
        new BufferService(() => _now).Enqueue(new JobEvent { JobName = "drop-me" }, BufferPath);
        var options = Options(maxAttempts: 1);
        options = options with { DeadLetterEnabled = false };
        var worker = Worker(AlwaysFails, options);

        var result = await worker.DrainOnceAsync();

        Assert.Equal(1, result.Dropped);
        Assert.Empty(new BufferService().List(BufferPath));
        Assert.Empty(new BufferService().List(DeadLetterPath, deadLetter: true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
