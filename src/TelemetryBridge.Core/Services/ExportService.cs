using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

public sealed class ExportService
{
    private const string ActivitySourceName = "TelemetryBridge.Cli";

    public async Task<ExportResult> TrySendAsync(JobEvent telemetryEvent, BridgeConfig config, bool verbose, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.Signoz.Endpoint))
        {
            return ExportResult.Failed("Missing signoz.endpoint in configuration.");
        }

        var protocol = ResolveProtocol(config.Signoz.Protocol);
        if (protocol is null)
        {
            return ExportResult.Skipped($"Protocol '{config.Signoz.Protocol}' is not supported. Use 'grpc' or 'http'.");
        }

        if (verbose)
        {
            Console.WriteLine($"[verbose] exporting via OpenTelemetry OTLP ({config.Signoz.Protocol}) to {config.Signoz.Endpoint}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.Signoz.TimeoutSeconds)));

        try
        {
            using var source = new ActivitySource(ActivitySourceName);
            using var provider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("TelemetryBridge.Cli"))
                .AddSource(ActivitySourceName)
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(config.Signoz.Endpoint);
                    options.Protocol = protocol.Value;
                    options.TimeoutMilliseconds = Math.Max(1000, config.Signoz.TimeoutSeconds * 1000);
                })
                .Build();

            using var activity = source.StartActivity("job.run", ActivityKind.Internal);
            if (activity is null)
            {
                return ExportResult.Failed("Could not create telemetry activity for export.");
            }

            activity.SetTag("job.name", telemetryEvent.JobName);
            activity.SetTag("job.status", telemetryEvent.Status);
            activity.SetTag("job.system", telemetryEvent.System);
            if (telemetryEvent.DurationMs.HasValue) activity.SetTag("job.duration_ms", telemetryEvent.DurationMs.Value);
            activity.SetTag("job.run_id", telemetryEvent.JobRunId.ToString());
            activity.SetTag("event.finished_at", telemetryEvent.FinishedAt.ToString("O"));

            if (string.Equals(telemetryEvent.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                activity.SetStatus(ActivityStatusCode.Error);
            }
            else
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }

            activity.Stop();

            await provider.ForceFlushAsync(timeoutCts.Token);
            return ExportResult.Success("Delivered via OpenTelemetry OTLP exporter.");
        }
        catch (OperationCanceledException)
        {
            return ExportResult.Failed("Export timed out.");
        }
        catch (Exception ex)
        {
            return ExportResult.Failed(ex.Message);
        }
    }

    private static OtlpExportProtocol? ResolveProtocol(string? protocol)
    {
        if (string.Equals(protocol, "grpc", StringComparison.OrdinalIgnoreCase)) return OtlpExportProtocol.Grpc;
        if (string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)) return OtlpExportProtocol.HttpProtobuf;
        return null;
    }
}

public sealed record ExportResult(bool Success, bool Skipped, string Message)
{
    public static ExportResult Success(string message) => new(true, false, message);
    public static ExportResult Skipped(string reason) => new(false, true, reason);
    public static ExportResult Failed(string reason) => new(false, false, reason);
}
