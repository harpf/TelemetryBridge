using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Service.Hosting;

/// <summary>
/// One drain pass over the durable buffer. Wraps Core's <c>RetryWorker</c> so
/// <see cref="DrainWorker"/> depends on this seam rather than the concrete
/// worker + live exporter, which keeps the background loop unit-testable.
/// </summary>
public interface IBufferDrainer
{
    Task<DrainResult> DrainOnceAsync(CancellationToken cancellationToken = default);
}
