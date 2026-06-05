namespace TelemetryBridge.Core.Models;

/// <summary>
/// Lifecycle state of a buffered telemetry item as it moves through the
/// durable queue: freshly enqueued (<see cref="Pending"/>), awaiting another
/// delivery attempt after a failure (<see cref="Retrying"/>), or parked after
/// exhausting its attempts / age budget (<see cref="DeadLetter"/>).
/// </summary>
public enum BufferItemState
{
    Pending,
    Retrying,
    DeadLetter
}

/// <summary>
/// A single buffered event on disk together with the durable retry metadata
/// (attempt count, next-eligible time) carried in its file name.
/// </summary>
public sealed record BufferItem
{
    /// <summary>Absolute path to the backing event file.</summary>
    public required string Path { get; init; }

    /// <summary>The deserialized telemetry event.</summary>
    public required JobEvent Event { get; init; }

    /// <summary>How many delivery attempts have already failed.</summary>
    public int Attempts { get; init; }

    /// <summary>When the item was first enqueued (used for max-age checks).</summary>
    public DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>Earliest time the item may be retried, or null if eligible now.</summary>
    public DateTimeOffset? NextEligibleAt { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public BufferItemState State { get; init; }

    /// <summary>Size of the backing file in bytes.</summary>
    public long SizeBytes { get; init; }
}
