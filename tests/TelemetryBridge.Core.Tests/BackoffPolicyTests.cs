using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

public sealed class BackoffPolicyTests
{
    private static BackoffPolicy Policy(double rng) =>
        new(baseDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(60), rng: () => rng);

    [Fact]
    public void Delay_GrowsExponentially_AtTheUpperJitterBound()
    {
        // rng == 1.0 yields the full (un-jittered) exponential delay.
        var policy = Policy(rng: 1.0);

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(1));  // base * 2^0
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(2));  // base * 2^1
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(3));  // base * 2^2
        Assert.Equal(TimeSpan.FromSeconds(8), policy.GetDelay(4));  // base * 2^3
    }

    [Fact]
    public void Delay_IsHalvedAtTheLowerJitterBound()
    {
        // rng == 0.0 yields the lower bound: half of the exponential delay.
        var policy = Policy(rng: 0.0);

        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(3)); // 4s * 0.5
    }

    [Fact]
    public void Delay_IsCappedAtMaxDelay()
    {
        var policy = Policy(rng: 1.0);

        // base * 2^9 = 512s, far past the 60s cap.
        Assert.Equal(TimeSpan.FromSeconds(60), policy.GetDelay(10));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(20)]
    public void Delay_AlwaysWithinHalfToFullExponentialBounds(int attempt)
    {
        var baseDelay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(60);

        // Sample several jitter values across the [0,1) range.
        foreach (var r in new[] { 0.0, 0.25, 0.5, 0.75, 0.999 })
        {
            var policy = new BackoffPolicy(baseDelay, maxDelay, rng: () => r);
            var delay = policy.GetDelay(attempt);

            var exponential = Math.Min(
                maxDelay.TotalMilliseconds,
                baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));

            Assert.InRange(
                delay.TotalMilliseconds,
                exponential / 2.0,
                exponential);
        }
    }

    [Fact]
    public void GetDelay_OnNonPositiveAttempt_ReturnsBaseBound()
    {
        var policy = Policy(rng: 1.0);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(0));
    }
}
