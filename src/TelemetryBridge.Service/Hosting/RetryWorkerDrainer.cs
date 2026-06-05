using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Configuration;
using TelemetryBridge.Service.Telemetry;

namespace TelemetryBridge.Service.Hosting;

/// <summary>
/// Adapts Core's <c>RetryWorker</c> to <see cref="IBufferDrainer"/>, binding it
/// to the singleton exporter and the buffer/backoff options from config. The
/// worker is reused as-is — the service contributes only the hoisted exporter
/// and the schedule.
/// </summary>
public sealed class RetryWorkerDrainer : IBufferDrainer
{
    private readonly RetryWorker _worker;

    public RetryWorkerDrainer(BufferService buffer, IJobEventExporter exporter, ServiceHostOptions options)
    {
        _worker = new RetryWorker(
            buffer,
            (evt, ct) => exporter.SendAsync(evt, ct),
            BackoffPolicy.Default(),
            RetryOptions.FromConfig(options.Bridge.Buffer));
    }

    public Task<DrainResult> DrainOnceAsync(CancellationToken cancellationToken = default) =>
        _worker.DrainOnceAsync(cancellationToken);
}
