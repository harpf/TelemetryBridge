using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
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

    // ===================================================================
    //  Logs & metrics (additive). TrySendAsync above and its span mapping
    //  are intentionally left untouched; the methods below add the new
    //  signals using the same protocol/endpoint/resource conventions.
    // ===================================================================

    /// <summary>
    /// Best-effort OTLP logs export for a single <see cref="LogEvent"/>. Mirrors
    /// the early-exit behavior of <see cref="TrySendAsync"/> (missing endpoint →
    /// failed, unsupported protocol → skipped) and never throws.
    /// </summary>
    public async Task<ExportResult> TrySendLogAsync(
        LogEvent logEvent,
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

        var timeoutMs = Math.Max(1000, config.Signoz.TimeoutSeconds * 1000);

        try
        {
            var endpoint = BuildSignalEndpoint(config.Signoz.Endpoint, protocol.Value, "/v1/logs");
            var mapping = BuildLogMapping(logEvent);

            if (verbose)
            {
                Console.WriteLine(
                    $"[verbose] exporting log via OTLP {protocol.Value} to {endpoint}");
            }

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddOpenTelemetry(options =>
                {
                    options.IncludeFormattedMessage = true;
                    options.SetResourceBuilder(BuildResourceBuilder(config));
                    options.AddOtlpExporter(exporter =>
                        ConfigureOtlp(exporter, endpoint, protocol.Value, timeoutMs, config));
                });
            });

            var logger = loggerFactory.CreateLogger("TelemetryBridge.Cli");
            var state = mapping.Attributes
                .Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))
                .ToList();

#pragma warning disable CA2254 // body is the message; attributes ride on the state
            logger.Log(
                MapSeverityToLogLevel(mapping.Severity),
                eventId: default,
                state: state,
                exception: null,
                formatter: (_, _) => mapping.Body);
#pragma warning restore CA2254

            // Disposing the factory flushes the OpenTelemetry logger provider.
            loggerFactory.Dispose();

            await Task.CompletedTask;
            return ExportResult.Success("Delivered via OpenTelemetry OTLP log exporter.");
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
    /// Best-effort OTLP metrics export for a single <see cref="MetricEvent"/>.
    /// Mirrors the early-exit behavior of <see cref="TrySendAsync"/> and never
    /// throws. A <c>counter</c> kind is recorded as a delta counter; any other
    /// kind is emitted as a one-shot observable gauge.
    /// </summary>
    public async Task<ExportResult> TrySendMetricAsync(
        MetricEvent metricEvent,
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

        var timeoutMs = Math.Max(1000, config.Signoz.TimeoutSeconds * 1000);

        try
        {
            var endpoint = BuildSignalEndpoint(config.Signoz.Endpoint, protocol.Value, "/v1/metrics");
            var mapping = BuildMetricMapping(metricEvent);

            if (verbose)
            {
                Console.WriteLine(
                    $"[verbose] exporting metric via OTLP {protocol.Value} to {endpoint}");
            }

            var tags = mapping.Attributes
                .Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))
                .ToArray();

            using var meter = new Meter(MeterName);

            using var provider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(BuildResourceBuilder(config))
                .AddMeter(MeterName)
                .AddOtlpExporter((exporter, _) =>
                    ConfigureOtlp(exporter, endpoint, protocol.Value, timeoutMs, config))
                .Build();

            if (string.Equals(mapping.Kind, "counter", StringComparison.OrdinalIgnoreCase))
            {
                var counter = meter.CreateCounter<double>(mapping.Name, mapping.Unit);
                counter.Add(mapping.Value, tags);
            }
            else
            {
                // Gauge: an observable instrument the provider samples on flush.
                meter.CreateObservableGauge(
                    mapping.Name,
                    () => new Measurement<double>(mapping.Value, tags),
                    mapping.Unit);
            }

            var flushed = provider.ForceFlush(timeoutMs);
            if (!flushed)
            {
                return ExportResult.Failed("OpenTelemetry exporter did not flush before timeout.");
            }

            await Task.CompletedTask;
            return ExportResult.Success("Delivered via OpenTelemetry OTLP metric exporter.");
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
    /// Projects a <see cref="LogEvent"/> onto an OTel log body, severity and a
    /// flat attribute bag. Pure and side-effect free so it can be unit tested
    /// without an exporter — mirrors <see cref="BuildSpanMapping"/>.
    /// </summary>
    internal static LogMapping BuildLogMapping(LogEvent logEvent)
    {
        var attributes = new Dictionary<string, object?>();

        void SetString(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) attributes[key] = value;
        }

        SetString("event.type", logEvent.EventType);
        SetString("log.record_id", logEvent.LogRecordId);
        SetString("log.correlation_id", logEvent.CorrelationId);
        SetString("log.instance", logEvent.Instance);

        AppendAttributes(attributes, logEvent.Attributes);

        return new LogMapping
        {
            Body = logEvent.Body,
            Severity = logEvent.Severity,
            Attributes = attributes
        };
    }

    /// <summary>
    /// Projects a <see cref="MetricEvent"/> onto an OTel instrument name/value
    /// and a flat attribute bag. Pure and side-effect free — mirrors
    /// <see cref="BuildSpanMapping"/>.
    /// </summary>
    internal static MetricMapping BuildMetricMapping(MetricEvent metricEvent)
    {
        var attributes = new Dictionary<string, object?>();

        void SetString(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) attributes[key] = value;
        }

        SetString("event.type", metricEvent.EventType);
        SetString("metric.id", metricEvent.MetricId);
        SetString("metric.correlation_id", metricEvent.CorrelationId);
        SetString("metric.instance", metricEvent.Instance);

        AppendAttributes(attributes, metricEvent.Attributes);

        return new MetricMapping
        {
            Name = metricEvent.Name,
            Value = metricEvent.Value,
            Unit = metricEvent.Unit,
            Kind = metricEvent.Kind,
            Attributes = attributes
        };
    }

    /// <summary>
    /// Copies free-form attributes onto <paramref name="target"/> under the
    /// <c>attr.</c> prefix, normalizing <see cref="JsonElement"/> values and
    /// skipping nulls — the same rule <see cref="BuildSpanMapping"/> applies.
    /// </summary>
    private static void AppendAttributes(
        IDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?>? attributes)
    {
        if (attributes is null) return;

        foreach (var (key, raw) in attributes)
        {
            var value = NormalizeAttributeValue(raw);
            if (value is null) continue;
            target["attr." + key] = value;
        }
    }

    private const string MeterName = "TelemetryBridge.Cli";

    private static LogLevel MapSeverityToLogLevel(string severity) => severity?.ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Information,
        "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "fatal" => LogLevel.Critical,
        _ => LogLevel.Information
    };

    private static ResourceBuilder BuildResourceBuilder(BridgeConfig config) =>
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
            });

    private static void ConfigureOtlp(
        OtlpExporterOptions exporter,
        Uri endpoint,
        OtlpExportProtocol protocol,
        int timeoutMs,
        BridgeConfig config)
    {
        exporter.Endpoint = endpoint;
        exporter.Protocol = protocol;
        exporter.TimeoutMilliseconds = timeoutMs;
        if (!string.IsNullOrWhiteSpace(config.Signoz.Headers))
        {
            exporter.Headers = config.Signoz.Headers;
        }
    }

    /// <summary>
    /// Like <see cref="BuildEndpoint"/> but for an arbitrary OTLP signal path
    /// (<c>/v1/logs</c>, <c>/v1/metrics</c>). Only HTTP needs the suffix; gRPC
    /// endpoints are returned untouched.
    /// </summary>
    internal static Uri BuildSignalEndpoint(string endpoint, OtlpExportProtocol protocol, string signalPath)
    {
        var uri = new Uri(endpoint);

        if (protocol == OtlpExportProtocol.HttpProtobuf)
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            var normalizedSignal = "/" + signalPath.Trim('/');

            if (!path.EndsWith(normalizedSignal, StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri)
                {
                    Path = $"{path}{normalizedSignal}".TrimStart('/')
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

/// <summary>
/// The OTel log body / severity / attribute bag derived from a
/// <see cref="LogEvent"/> by <see cref="ExportService.BuildLogMapping"/>.
/// </summary>
internal sealed record LogMapping
{
    public required string Body { get; init; }
    public required string Severity { get; init; }
    public required IReadOnlyDictionary<string, object?> Attributes { get; init; }
}

/// <summary>
/// The OTel instrument name / value / unit / kind and attribute bag derived from
/// a <see cref="MetricEvent"/> by <see cref="ExportService.BuildMetricMapping"/>.
/// </summary>
internal sealed record MetricMapping
{
    public required string Name { get; init; }
    public required double Value { get; init; }
    public string? Unit { get; init; }
    public required string Kind { get; init; }
    public required IReadOnlyDictionary<string, object?> Attributes { get; init; }
}