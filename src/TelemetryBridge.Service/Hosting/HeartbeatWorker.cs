using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelemetryBridge.Service.Configuration;
using TelemetryBridge.Service.Health;

namespace TelemetryBridge.Service.Hosting;

/// <summary>
/// Emits a periodic heartbeat log carrying the current buffer status, so an
/// operator tailing the service log can see at a glance that the host is alive
/// and whether the queue is draining or backing up.
/// </summary>
public sealed class HeartbeatWorker : BackgroundService
{
    private readonly HealthReporter _health;
    private readonly ServiceHostOptions _options;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(HealthReporter health, ServiceHostOptions options, ILogger<HeartbeatWorker> logger)
    {
        _health = health;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);

        try
        {
            do
            {
                Beat();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void Beat()
    {
        try
        {
            var status = _health.Snapshot();
            _logger.LogInformation(
                "heartbeat: pending={Pending} retrying={Retrying} dead-letter={DeadLetter} size={Bytes}B ingest={Ingest}.",
                status.Pending, status.Retrying, status.DeadLetter, status.TotalBytes, _options.EnableHttpIngest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "heartbeat: could not read buffer status.");
        }
    }
}
