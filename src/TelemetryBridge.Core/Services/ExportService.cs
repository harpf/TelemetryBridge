using System.Diagnostics;
using System.Text.Json;
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

            var mapping = BuildSpanMapping(telemetryEvent);

            foreach (var (key, value) in mapping.Tags)
            {
                activity.SetTag(key, value);
            }

            if (mapping.ExceptionEvent is { } exceptionEvent)
            {
                var eventTags = new ActivityTagsCollection();
                foreach (var (key, value) in exceptionEvent)
                {
                    eventTags[key] = value;
                }
                activity.AddEvent(new ActivityEvent("exception", tags: eventTags));
            }

            activity.SetStatus(
                mapping.IsError ? ActivityStatusCode.Error : ActivityStatusCode.Ok,
                mapping.StatusDescription);

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

    /// <summary>
    /// Projects a <see cref="JobEvent"/> onto OpenTelemetry span tags and an
    /// optional exception event. Pure and side-effect free so it can be unit
    /// tested without standing up an exporter. Keeps the existing
    /// <c>event.type</c>/<c>job.*</c>/<c>error.message</c> tags and adds the
    /// new contract fields (correlation/instance/script/exit code), the
    /// structured error (<c>error.*</c> + exception event) and the free-form
    /// attributes (under the <c>attr.</c> prefix).
    /// </summary>
    internal static SpanMapping BuildSpanMapping(JobEvent telemetryEvent)
    {
        var tags = new Dictionary<string, object?>();

        void SetString(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) tags[key] = value;
        }

        SetString("event.type", telemetryEvent.EventType);
        SetString("job.name", telemetryEvent.JobName);
        SetString("job.status", telemetryEvent.Status);
        SetString("job.system", telemetryEvent.System);
        SetString("job.run_id", telemetryEvent.JobRunId);
        tags["job.started_at"] = telemetryEvent.StartedAt.ToString("O");

        if (telemetryEvent.FinishedAt.HasValue)
        {
            tags["job.finished_at"] = telemetryEvent.FinishedAt.Value.ToString("O");
        }

        if (telemetryEvent.DurationMs.HasValue)
        {
            tags["job.duration_ms"] = telemetryEvent.DurationMs.Value;
        }

        SetString("job.correlation_id", telemetryEvent.CorrelationId);
        SetString("job.parent_correlation_id", telemetryEvent.ParentCorrelationId);
        SetString("job.instance", telemetryEvent.Instance);
        SetString("job.script_path", telemetryEvent.ScriptPath);
        SetString("job.script_name", telemetryEvent.ScriptName);
        SetString("job.script_version", telemetryEvent.ScriptVersion);

        if (telemetryEvent.ExitCode.HasValue)
        {
            tags["job.exit_code"] = telemetryEvent.ExitCode.Value;
        }

        IReadOnlyDictionary<string, object?>? exceptionEvent = null;
        if (telemetryEvent.Error is { } error)
        {
            SetString("error.type", error.Type);
            SetString("error.message", error.Message);
            SetString("error.code", error.Code);
            SetString("error.category", error.Category);
            SetString("error.fully_qualified_id", error.FullyQualifiedId);
            if (error.ScriptLineNumber.HasValue) tags["error.script_line_number"] = error.ScriptLineNumber.Value;
            if (error.ScriptColumnNumber.HasValue) tags["error.script_column_number"] = error.ScriptColumnNumber.Value;
            SetString("error.command_name", error.CommandName);
            SetString("error.stack_trace", error.StackTrace);

            var eventTags = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(error.Type)) eventTags["exception.type"] = error.Type;
            if (!string.IsNullOrWhiteSpace(error.Message)) eventTags["exception.message"] = error.Message;
            if (!string.IsNullOrWhiteSpace(error.StackTrace)) eventTags["exception.stacktrace"] = error.StackTrace;
            exceptionEvent = eventTags;
        }

        if (telemetryEvent.Attributes is { } attributes)
        {
            foreach (var (key, raw) in attributes)
            {
                var value = NormalizeAttributeValue(raw);
                if (value is null) continue; // null-valued attributes are not emitted as tags
                tags["attr." + key] = value;
            }
        }

        var isError = string.Equals(telemetryEvent.Status, "failed", StringComparison.OrdinalIgnoreCase);

        return new SpanMapping
        {
            Tags = tags,
            ExceptionEvent = exceptionEvent,
            IsError = isError,
            StatusDescription = isError ? telemetryEvent.Error?.Message : null
        };
    }

    /// <summary>
    /// Unwraps an attribute value into an OTel-friendly primitive. Values that
    /// arrive from disk are <see cref="JsonElement"/>; values built in-process
    /// are already primitives and pass through untouched.
    /// </summary>
    internal static object? NormalizeAttributeValue(object? raw)
    {
        if (raw is not JsonElement element) return raw;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
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

/// <summary>
/// The span tags / exception event derived from a <see cref="JobEvent"/> by
/// <see cref="ExportService.BuildSpanMapping"/>.
/// </summary>
internal sealed record SpanMapping
{
    public required IReadOnlyDictionary<string, object?> Tags { get; init; }

    /// <summary>Exception event tags, or null when the event carries no error.</summary>
    public IReadOnlyDictionary<string, object?>? ExceptionEvent { get; init; }

    public bool IsError { get; init; }

    public string? StatusDescription { get; init; }
}