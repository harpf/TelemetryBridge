using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

public sealed class BufferQueueTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tb-queue-tests", Guid.NewGuid().ToString("N"));

    private string BufferPath => Path.Combine(_root, "buffer");
    private string DeadLetterPath => Path.Combine(_root, "dead-letter");

    private static BufferService ServiceAt(DateTimeOffset now) =>
        new(() => now);

    [Fact]
    public void List_ReturnsEnqueuedItems_AsPendingWithZeroAttempts()
    {
        var now = DateTimeOffset.Parse("2026-06-05T10:00:00Z");
        var service = ServiceAt(now);
        service.Enqueue(new JobEvent { JobName = "nightly-backup" }, BufferPath);

        var items = service.List(BufferPath);

        var item = Assert.Single(items);
        Assert.Equal("nightly-backup", item.Event.JobName);
        Assert.Equal(0, item.Attempts);
        Assert.Equal(BufferItemState.Pending, item.State);
        Assert.Null(item.NextEligibleAt);
        Assert.Equal(now, item.EnqueuedAt);
    }

    [Fact]
    public void List_OnMissingFolder_ReturnsEmpty()
    {
        var service = new BufferService();
        Assert.Empty(service.List(BufferPath));
    }

    [Fact]
    public void List_IgnoresStrayTempOrPartialFiles()
    {
        var service = new BufferService();
        service.Enqueue(new JobEvent { JobName = "real" }, BufferPath);

        // Simulate a crash mid-write: a half-written temp file left behind.
        File.WriteAllText(Path.Combine(BufferPath, "garbage.json.tmp"), "{ \"jobName\": \"half");
        File.WriteAllText(Path.Combine(BufferPath, "notes.txt"), "not a buffer file");

        var items = service.List(BufferPath);

        var item = Assert.Single(items);
        Assert.Equal("real", item.Event.JobName);
    }

    [Fact]
    public void Read_ReturnsTheStoredJobEvent()
    {
        var service = new BufferService();
        var path = service.Enqueue(new JobEvent { JobName = "job-a", Status = "failed" }, BufferPath);

        var evt = service.Read(path);

        Assert.NotNull(evt);
        Assert.Equal("job-a", evt!.JobName);
        Assert.Equal("failed", evt.Status);
    }

    [Fact]
    public void List_ReturnsItemsInEnqueueOrder()
    {
        var t0 = DateTimeOffset.Parse("2026-06-05T10:00:00Z");
        ServiceAt(t0).Enqueue(new JobEvent { JobName = "first" }, BufferPath);
        ServiceAt(t0.AddSeconds(1)).Enqueue(new JobEvent { JobName = "second" }, BufferPath);
        ServiceAt(t0.AddSeconds(2)).Enqueue(new JobEvent { JobName = "third" }, BufferPath);

        var names = new BufferService().List(BufferPath).Select(i => i.Event.JobName).ToArray();

        Assert.Equal(new[] { "first", "second", "third" }, names);
    }

    [Fact]
    public void MarkRetrying_PersistsAttemptCountAndNextEligibleTime()
    {
        var now = DateTimeOffset.Parse("2026-06-05T10:00:00Z");
        var service = ServiceAt(now);
        service.Enqueue(new JobEvent { JobName = "flaky" }, BufferPath);
        var item = service.List(BufferPath).Single();

        var nextEligible = now.AddSeconds(30);
        service.MarkRetrying(item, attempts: 1, nextEligibleAt: nextEligible);

        // Reload from disk: the new state must be durable (survives a restart).
        var reloaded = new BufferService().List(BufferPath).Single();
        Assert.Equal(1, reloaded.Attempts);
        Assert.Equal(BufferItemState.Retrying, reloaded.State);
        Assert.Equal(nextEligible, reloaded.NextEligibleAt);
        Assert.Equal("flaky", reloaded.Event.JobName);
    }

    [Fact]
    public void MoveToDeadLetter_RemovesFromBuffer_AndAppearsInDeadLetter()
    {
        var service = new BufferService();
        service.Enqueue(new JobEvent { JobName = "doomed" }, BufferPath);
        var item = service.List(BufferPath).Single();

        service.MoveToDeadLetter(item, DeadLetterPath);

        Assert.Empty(service.List(BufferPath));
        var dead = service.List(DeadLetterPath, deadLetter: true).Single();
        Assert.Equal("doomed", dead.Event.JobName);
        Assert.Equal(BufferItemState.DeadLetter, dead.State);
    }

    [Fact]
    public void ResetToPending_MovesDeadLetterItemBack_WithAttemptsCleared()
    {
        var now = DateTimeOffset.Parse("2026-06-05T10:00:00Z");
        var service = ServiceAt(now);
        service.Enqueue(new JobEvent { JobName = "recover-me" }, BufferPath);
        var item = service.List(BufferPath).Single();
        service.MarkRetrying(item, attempts: 3, nextEligibleAt: now.AddMinutes(5));
        var retrying = service.List(BufferPath).Single();
        service.MoveToDeadLetter(retrying, DeadLetterPath);
        var dead = service.List(DeadLetterPath, deadLetter: true).Single();

        service.ResetToPending(dead, BufferPath);

        Assert.Empty(service.List(DeadLetterPath, deadLetter: true));
        var back = service.List(BufferPath).Single();
        Assert.Equal("recover-me", back.Event.JobName);
        Assert.Equal(0, back.Attempts);
        Assert.Equal(BufferItemState.Pending, back.State);
        Assert.Null(back.NextEligibleAt);
    }

    [Fact]
    public void Delete_RemovesItemFile()
    {
        var service = new BufferService();
        service.Enqueue(new JobEvent { JobName = "gone" }, BufferPath);
        var item = service.List(BufferPath).Single();

        service.Delete(item.Path);

        Assert.Empty(service.List(BufferPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
