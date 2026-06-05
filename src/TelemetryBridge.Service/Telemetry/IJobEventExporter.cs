using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Service.Telemetry;

/// <summary>
/// Sends a single <see cref="JobEvent"/> and reports the outcome using Core's
/// <see cref="ExportResult"/> so it slots straight into <c>RetryWorker</c>'s
/// exporter delegate. Abstracted so the drain worker can be unit tested against
/// a fake exporter with no networking.
/// </summary>
public interface IJobEventExporter
{
    Task<ExportResult> SendAsync(JobEvent telemetryEvent, CancellationToken cancellationToken = default);
}
