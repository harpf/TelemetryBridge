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
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public async Task<ExportResult> TrySendAsync(
        JobEvent telemetryEvent,
        BridgeConfig config,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.Signoz.Endpoint))
        {
            return ExportResult.Failed("Missing signoz.endpoint in configuration.");
        }

        var protocol = ResolveProtocol(config.Signoz.Protocol);
        if (protocol is null)
        {
            return ExportResult.Skipped(
                $"Protocol '{config.Signoz.Protocol}' is not supported. Use 'grpc' or 'http'.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.Signoz.TimeoutSeconds)));

        try
        {
            var endpoint = BuildEndpoint(config.Signoz.Endpoint, protocol.Value);

            if (verbose)
            {
                Console.WriteLine(
                    $"[verbose] exporting trace via OTLP {protocol.Value} to {endpoint}");
            }

            using var provider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(
                    ResourceBuilder.CreateDefault()
                        .AddService(
                            serviceName: "TelemetryBridge.Cli",
                            serviceInstanceId: config.Agent.InstanceId == "auto"
                                ? Environment.MachineName
                                : config.Agent.InstanceId)
                        .AddAttributes(new Dictionary<string, object>
                        {
                            ["telemetry.sdk.bridge"] = "TelemetryBridge",
                            ["host.name"] = Environment.MachineName
                        }))
                .AddSource(ActivitySourceName)
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = endpoint;
                    options.Protocol = protocol.Value;
                    options.TimeoutMilliseconds = Math.Max(
                        1000,
                        config.Signoz.TimeoutSeconds * 1000);

                    // Für SigNoz Cloud:
                    // options.Headers = "signoz-ingestion-key=<your-ingestion-key>";
                    //
                    // Besser aus Config:
                    if (!string.IsNullOrWhiteSpace(config.Signoz.Headers))
                    {
                        options.Headers = config.Signoz.Headers;
                    }
                })
                .Build();

            var startedAt = telemetryEvent.StartedAt.UtcDateTime;

            using var activity = ActivitySource.StartActivity(
                "job.run",
                ActivityKind.Internal,
                parentContext: default,
                tags: null,
                links: null,
                startTime: startedAt);

            if (activity is null)
            {
                return ExportResult.Failed("Could not create telemetry activity for export.");
            }

            activity.SetTag("event.type", telemetryEvent.EventType);
            activity.SetTag("job.name", telemetryEvent.JobName);
            activity.SetTag("job.status", telemetryEvent.Status);
            activity.SetTag("job.system", telemetryEvent.System);
            activity.SetTag("job.run_id", telemetryEvent.JobRunId);

            if (!string.IsNullOrWhiteSpace(telemetryEvent.RunbookName)) activity.SetTag("runbook.name", telemetryEvent.RunbookName);
            if (!string.IsNullOrWhiteSpace(telemetryEvent.ScriptPath)) activity.SetTag("script.path", telemetryEvent.ScriptPath);
            if (!string.IsNullOrWhiteSpace(telemetryEvent.EnvironmentName)) activity.SetTag("environment.name", telemetryEvent.EnvironmentName);
            if (!string.IsNullOrWhiteSpace(telemetryEvent.HostName)) activity.SetTag("host.name", telemetryEvent.HostName);

            foreach (var attribute in telemetryEvent.Attributes)
            {
                activity.SetTag($"script.attr.{attribute.Key}", attribute.Value);
            }

            activity.SetTag("job.started_at", telemetryEvent.StartedAt.ToString("O"));

            if (telemetryEvent.FinishedAt.HasValue)
            {
                activity.SetTag("job.finished_at", telemetryEvent.FinishedAt.Value.ToString("O"));
            }

            if (telemetryEvent.DurationMs.HasValue)
            {
                activity.SetTag("job.duration_ms", telemetryEvent.DurationMs.Value);
            }

            if (!string.IsNullOrWhiteSpace(telemetryEvent.ErrorMessage))
            {
                activity.SetTag("error.message", telemetryEvent.ErrorMessage);
                activity.AddEvent(new ActivityEvent(
                    "exception",
                    tags: new ActivityTagsCollection
                    {
                        ["exception.message"] = telemetryEvent.ErrorMessage
                    }));
            }

            var failed = string.Equals(
                telemetryEvent.Status,
                "failed",
                StringComparison.OrdinalIgnoreCase);

            activity.SetStatus(
                failed ? ActivityStatusCode.Error : ActivityStatusCode.Ok,
                failed ? telemetryEvent.ErrorMessage : null);

            if (telemetryEvent.FinishedAt.HasValue)
            {
                activity.SetEndTime(telemetryEvent.FinishedAt.Value.UtcDateTime);
            }

            activity.Stop();

            var flushed = provider.ForceFlush(
                Math.Max(1000, config.Signoz.TimeoutSeconds * 1000));

            if (!flushed)
            {
                return ExportResult.Failed("OpenTelemetry exporter did not flush before timeout.");
            }

            await Task.CompletedTask;
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

    internal static OtlpExportProtocol? ResolveProtocol(string? protocol)
    {
        if (string.Equals(protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return OtlpExportProtocol.Grpc;
        }

        if (string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase))
        {
            return OtlpExportProtocol.HttpProtobuf;
        }

        return null;
    }

    internal static Uri BuildEndpoint(string endpoint, OtlpExportProtocol protocol)
    {
        var uri = new Uri(endpoint);

        if (protocol == OtlpExportProtocol.HttpProtobuf)
        {
            var path = uri.AbsolutePath.TrimEnd('/');

            if (!path.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri)
                {
                    Path = $"{path}/v1/traces".TrimStart('/')
                };

                return builder.Uri;
            }
        }

        return uri;
    }
}

public sealed record ExportResult(bool IsSuccess, bool IsSkipped, string Message)
{
    public static ExportResult Success(string message) => new(true, false, message);
    public static ExportResult Skipped(string reason) => new(false, true, reason);
    public static ExportResult Failed(string reason) => new(false, false, reason);
}