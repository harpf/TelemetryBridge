using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

/// <summary>
/// Tunables that drive a drain pass. Built from <see cref="BridgeConfig.BufferOptions"/>
/// but kept as a separate record so the worker has no opinion on config loading.
/// </summary>
public sealed record RetryOptions
{
    public required string BufferPath { get; init; }
    public required string DeadLetterPath { get; init; }
    public bool DeadLetterEnabled { get; init; } = true;
    public int MaxAttempts { get; init; } = 10;
    public int MaxEventAgeHours { get; init; } = 72;
    public int BatchSize { get; init; } = 50;

    public static RetryOptions FromConfig(BridgeConfig.BufferOptions buffer) => new()
    {
        BufferPath = buffer.Path,
        DeadLetterPath = buffer.DeadLetterPath,
        DeadLetterEnabled = buffer.DeadLetterEnabled,
        MaxAttempts = buffer.MaxAttempts,
        MaxEventAgeHours = buffer.MaxEventAgeHours,
        BatchSize = buffer.BatchSize
    };
}

/// <summary>Outcome counts from a single drain pass.</summary>
public sealed record DrainResult
{
    /// <summary>Items exported successfully and removed from the buffer.</summary>
    public int Delivered { get; init; }

    /// <summary>Items that failed and were scheduled for a later retry.</summary>
    public int Retried { get; init; }

    /// <summary>Items moved to the dead-letter folder (attempts/age exhausted).</summary>
    public int DeadLettered { get; init; }

    /// <summary>Exhausted items discarded because dead-lettering is disabled.</summary>
    public int Dropped { get; init; }

    /// <summary>Items skipped because their export was reported as not-applicable (config).</summary>
    public int Skipped { get; init; }

    /// <summary>Items not yet eligible (still inside their backoff window).</summary>
    public int NotEligible { get; init; }
}

/// <summary>
/// Drains the durable buffer: for each eligible item it attempts delivery,
/// deletes on success, and on failure applies exponential backoff with jitter,
/// dead-lettering once an item exhausts its attempt or age budget.
///
/// The clock, the exporter, and the backoff policy are all injected so the loop
/// is deterministically testable without networking or real sleeping.
/// </summary>
public sealed class RetryWorker
{
    private readonly BufferService _buffer;
    private readonly Func<JobEvent, CancellationToken, Task<ExportResult>> _exporter;
    private readonly BackoffPolicy _backoff;
    private readonly RetryOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    public RetryWorker(
        BufferService buffer,
        Func<JobEvent, CancellationToken, Task<ExportResult>> exporter,
        BackoffPolicy backoff,
        RetryOptions options,
        Func<DateTimeOffset>? clock = null)
    {
        _buffer = buffer;
        _exporter = exporter;
        _backoff = backoff;
        _options = options;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Runs a single drain pass and returns the outcome counts.</summary>
    public async Task<DrainResult> DrainOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock();
        var maxAge = TimeSpan.FromHours(Math.Max(1, _options.MaxEventAgeHours));

        int delivered = 0, retried = 0, deadLettered = 0, dropped = 0, skipped = 0, notEligible = 0;
        var attempted = 0;

        foreach (var item in _buffer.List(_options.BufferPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Age budget is checked first so stale items leave the queue even if
            // they are still inside a backoff window — and without an export call.
            if (now - item.EnqueuedAt > maxAge)
            {
                if (Retire(item)) deadLettered++;
                else dropped++;
                continue;
            }

            // Respect the backoff schedule.
            if (item.NextEligibleAt is { } next && next > now)
            {
                notEligible++;
                continue;
            }

            if (attempted >= _options.BatchSize) continue;
            attempted++;

            var result = await _exporter(item.Event, cancellationToken);

            if (result.IsSuccess)
            {
                _buffer.Delete(item.Path);
                delivered++;
                continue;
            }

            if (result.IsSkipped)
            {
                // Not a delivery failure (e.g. unsupported protocol). Leave the
                // item untouched so a fixed config can deliver it later.
                skipped++;
                continue;
            }

            var attempts = item.Attempts + 1;
            if (attempts >= _options.MaxAttempts)
            {
                if (Retire(item)) deadLettered++;
                else dropped++;
            }
            else
            {
                var nextEligible = now + _backoff.GetDelay(attempts);
                _buffer.MarkRetrying(item, attempts, nextEligible);
                retried++;
            }
        }

        return new DrainResult
        {
            Delivered = delivered,
            Retried = retried,
            DeadLettered = deadLettered,
            Dropped = dropped,
            Skipped = skipped,
            NotEligible = notEligible
        };
    }

    /// <summary>
    /// Removes an exhausted item from the active queue: parks it in the
    /// dead-letter folder when enabled, otherwise drops it. Returns true if it
    /// was dead-lettered, false if dropped.
    /// </summary>
    private bool Retire(BufferItem item)
    {
        if (_options.DeadLetterEnabled)
        {
            _buffer.MoveToDeadLetter(item, _options.DeadLetterPath);
            return true;
        }

        _buffer.Delete(item.Path);
        return false;
    }
}
