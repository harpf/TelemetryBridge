namespace TelemetryBridge.Core.Services;

/// <summary>
/// Computes an exponential backoff delay with "equal jitter": the delay for a
/// given attempt is half the exponential value plus a random portion of the
/// other half, capped at a maximum. The randomness source is injectable so the
/// retry worker stays deterministically testable (no real sleeping).
///
///   exponential = min(maxDelay, baseDelay * 2^(attempt-1))
///   delay       = exponential/2 + rng() * exponential/2   where rng() in [0,1)
/// </summary>
public sealed class BackoffPolicy
{
    private readonly double _baseMs;
    private readonly double _maxMs;
    private readonly Func<double> _rng;

    public BackoffPolicy(TimeSpan baseDelay, TimeSpan maxDelay, Func<double>? rng = null)
    {
        _baseMs = Math.Max(0, baseDelay.TotalMilliseconds);
        _maxMs = Math.Max(_baseMs, maxDelay.TotalMilliseconds);
        _rng = rng ?? Random.Shared.NextDouble;
    }

    /// <summary>A sensible default: 5s base, capped at 5 minutes, full random jitter.</summary>
    public static BackoffPolicy Default() =>
        new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

    /// <summary>
    /// Delay before the given attempt (1-based). The result lies in
    /// [exponential/2, exponential], where exponential is the capped
    /// exponential value for that attempt.
    /// </summary>
    public TimeSpan GetDelay(int attempt)
    {
        var exponent = Math.Max(0, attempt - 1);
        var exponential = Math.Min(_maxMs, _baseMs * Math.Pow(2, exponent));
        var half = exponential / 2.0;
        var jittered = half + (_rng() * half);
        return TimeSpan.FromMilliseconds(jittered);
    }
}
