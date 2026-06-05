using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Configuration;

namespace TelemetryBridge.Service.Hosting;

/// <summary>
/// Owns the retry loop. On <c>buffer.flushIntervalSeconds</c> it runs one drain
/// pass over the durable buffer (delivering, scheduling retries, dead-lettering)
/// using the singleton exporter. On shutdown it runs one final, time-bounded
/// pass so in-flight items get a last delivery attempt before the host stops.
/// </summary>
public sealed class DrainWorker : BackgroundService
{
    // Cap the shutdown drain so StopAsync can't hang the service host.
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(15);

    private readonly IBufferDrainer _drainer;
    private readonly ServiceHostOptions _options;
    private readonly ILogger<DrainWorker> _logger;

    public DrainWorker(IBufferDrainer drainer, ServiceHostOptions options, ILogger<DrainWorker> logger)
    {
        _drainer = drainer;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Drain worker started; interval {IntervalSeconds}s, buffer {BufferPath}.",
            _options.FlushInterval.TotalSeconds,
            _options.Bridge.Buffer.Path);

        using var timer = new PeriodicTimer(_options.FlushInterval);

        // Drain once at startup so a backlog left by a crash is worked off
        // immediately rather than after the first interval.
        await DrainSafelyAsync(stoppingToken);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await DrainSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Drain worker stopping; running final flush.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ShutdownDrainTimeout);

        try
        {
            var result = await _drainer.DrainOnceAsync(timeout.Token);
            LogResult("final flush", result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Final flush did not complete within {Timeout}.", ShutdownDrainTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Final flush failed.");
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task DrainSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _drainer.DrainOnceAsync(cancellationToken);
            LogResult("drain pass", result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A drain pass must never take the worker down — log and wait for the
            // next tick.
            _logger.LogError(ex, "Drain pass failed; will retry next interval.");
        }
    }

    private void LogResult(string label, DrainResult result)
    {
        var touched = result.Delivered + result.Retried + result.DeadLettered +
                      result.Dropped + result.Skipped;

        if (touched == 0)
        {
            _logger.LogDebug("{Label}: nothing to do ({NotEligible} not yet eligible).", label, result.NotEligible);
            return;
        }

        _logger.LogInformation(
            "{Label}: delivered={Delivered} retried={Retried} dead-lettered={DeadLettered} " +
            "dropped={Dropped} skipped={Skipped} not-eligible={NotEligible}.",
            label, result.Delivered, result.Retried, result.DeadLettered,
            result.Dropped, result.Skipped, result.NotEligible);
    }
}
