using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

public sealed class ExportService
{
    private const string ActivitySourceName = "TelemetryBridge.Cli";

    public Task<ExportResult> TrySendAsync(JobEvent telemetryEvent, BridgeConfig config, bool verbose, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.Signoz.Endpoint))
        {
            return Task.FromResult(ExportResult.CreateFailed("Missing signoz.endpoint in configuration."));
        }

        var protocol = ResolveProtocol(config.Signoz.Protocol);
        if (protocol is null)
        {
            return Task.FromResult(ExportResult.CreateSkipped($"Protocol '{config.Signoz.Protocol}' is not supported. Use 'grpc' or 'http'."));
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
                return Task.FromResult(ExportResult.CreateFailed("Could not create telemetry activity for export."));
            }

            activity.SetTag("job.name", telemetryEvent.JobName);
            activity.SetTag("job.status", telemetryEvent.Status);
            activity.SetTag("job.system", telemetryEvent.System);
            if (telemetryEvent.DurationMs.HasValue) activity.SetTag("job.duration_ms", telemetryEvent.DurationMs.Value);
            activity.SetTag("job.run_id", telemetryEvent.JobRunId.ToString());
            if (telemetryEvent.FinishedAt.HasValue) activity.SetTag("event.finished_at", telemetryEvent.FinishedAt.Value.ToString("O"));

            if (string.Equals(telemetryEvent.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                activity.SetStatus(ActivityStatusCode.Error);
            }
            else
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }

            activity.Stop();
            provider.ForceFlush((int)Math.Max(1000, config.Signoz.TimeoutSeconds * 1000));
            return Task.FromResult(ExportResult.CreateSuccess("Delivered via OpenTelemetry OTLP exporter."));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(ExportResult.CreateFailed("Export timed out."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ExportResult.CreateFailed(ex.Message));
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
    public static ExportResult CreateSuccess(string message) => new(true, false, message);
    public static ExportResult CreateSkipped(string reason) => new(false, true, reason);
    public static ExportResult CreateFailed(string reason) => new(false, false, reason);
}
