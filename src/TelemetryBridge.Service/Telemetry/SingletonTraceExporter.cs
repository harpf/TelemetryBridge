using System.Diagnostics;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;
// Disambiguate from OpenTelemetry.ExportResult, which the OTel usings also bring in.
using ExportResult = TelemetryBridge.Core.Services.ExportResult;

namespace TelemetryBridge.Service.Telemetry;

/// <summary>
/// The service host's export path. Unlike the one-shot CLI — which builds and
/// tears down a <see cref="TracerProvider"/> and OTLP exporter for every single
/// event — this builds the provider <b>once</b> and reuses it for the lifetime
/// of the service. That is the perf win the improvement plan (§3.6) calls out:
/// the exporter connection / batch processor are hoisted to a singleton instead
/// of being rebuilt per event on every drain pass.
///
/// The per-event work (map the <see cref="JobEvent"/> onto a span, flush, report
/// success/failure) mirrors <c>ExportService.TrySendAsync</c> so a buffered
/// event delivered by the service is indistinguishable from one delivered by the
/// CLI. <c>ExportService</c> is left untouched (a sibling workspace owns it).
/// </summary>
public sealed class SingletonTraceExporter : IJobEventExporter, IAsyncDisposable, IDisposable
{
    public const string ActivitySourceName = "TelemetryBridge.Service";

    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private readonly TracerProvider? _provider;
    private readonly BridgeConfig _config;
    private readonly int _flushTimeoutMs;

    public SingletonTraceExporter(BridgeConfig config)
    {
        _config = config;
        _flushTimeoutMs = Math.Max(1000, config.Signoz.TimeoutSeconds * 1000);

        // Resolve the protocol up front; if it's unsupported we still construct
        // (so the host starts) but every send short-circuits to Skipped, exactly
        // like the CLI path — a fixed config can then deliver the buffered items.
        var protocol = ResolveProtocol(config.Signoz.Protocol);
        if (protocol is null || string.IsNullOrWhiteSpace(config.Signoz.Endpoint))
        {
            _provider = null;
            return;
        }

        var endpoint = BuildEndpoint(config.Signoz.Endpoint, protocol.Value);
        var instanceId = config.Agent.InstanceId == "auto"
            ? Environment.MachineName
            : config.Agent.InstanceId;
        var serviceName = string.IsNullOrWhiteSpace(config.Service.Name)
            ? "TelemetryBridge.Service"
            : config.Service.Name;

        _provider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(
                ResourceBuilder.CreateDefault()
                    .AddService(serviceName: serviceName, serviceInstanceId: instanceId)
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
                options.TimeoutMilliseconds = _flushTimeoutMs;
                if (!string.IsNullOrWhiteSpace(config.Signoz.Headers))
                {
                    options.Headers = config.Signoz.Headers;
                }
            })
            .Build();
    }

    public Task<ExportResult> SendAsync(JobEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_config.Signoz.Endpoint))
        {
            return Task.FromResult(ExportResult.Failed("Missing signoz.endpoint in configuration."));
        }

        if (ResolveProtocol(_config.Signoz.Protocol) is null || _provider is null)
        {
            return Task.FromResult(ExportResult.Skipped(
                $"Protocol '{_config.Signoz.Protocol}' is not supported. Use 'grpc' or 'http'."));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var activity = _activitySource.StartActivity(
                "job.run",
                ActivityKind.Internal,
                parentContext: default,
                tags: null,
                links: null,
                startTime: telemetryEvent.StartedAt.UtcDateTime);

            if (activity is null)
            {
                return Task.FromResult(ExportResult.Failed("Could not create telemetry activity for export."));
            }

            ApplySpan(activity, telemetryEvent);

            if (telemetryEvent.FinishedAt.HasValue)
            {
                activity.SetEndTime(telemetryEvent.FinishedAt.Value.UtcDateTime);
            }

            activity.Stop();

            var flushed = _provider.ForceFlush(_flushTimeoutMs);
            return Task.FromResult(flushed
                ? ExportResult.Success("Delivered via singleton OTLP exporter.")
                : ExportResult.Failed("OpenTelemetry exporter did not flush before timeout."));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(ExportResult.Failed("Export timed out."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ExportResult.Failed(ex.Message));
        }
    }

    /// <summary>
    /// Projects the event onto span tags + status + an optional exception event.
    /// Mirrors <c>ExportService.BuildSpanMapping</c> (which is internal to Core)
    /// so the on-wire shape stays identical between the CLI and the service.
    /// </summary>
    private static void ApplySpan(Activity activity, JobEvent evt)
    {
        void SetString(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) activity.SetTag(key, value);
        }

        SetString("event.type", evt.EventType);
        SetString("job.name", evt.JobName);
        SetString("job.status", evt.Status);
        SetString("job.system", evt.System);
        SetString("job.run_id", evt.JobRunId);
        activity.SetTag("job.started_at", evt.StartedAt.ToString("O"));

        if (evt.FinishedAt.HasValue) activity.SetTag("job.finished_at", evt.FinishedAt.Value.ToString("O"));
        if (evt.DurationMs.HasValue) activity.SetTag("job.duration_ms", evt.DurationMs.Value);

        SetString("job.correlation_id", evt.CorrelationId);
        SetString("job.parent_correlation_id", evt.ParentCorrelationId);
        SetString("job.instance", evt.Instance);
        SetString("job.script_path", evt.ScriptPath);
        SetString("job.script_name", evt.ScriptName);
        SetString("job.script_version", evt.ScriptVersion);

        if (evt.ExitCode.HasValue) activity.SetTag("job.exit_code", evt.ExitCode.Value);

        if (evt.Error is { } error)
        {
            SetString("error.type", error.Type);
            SetString("error.message", error.Message);
            SetString("error.code", error.Code);
            SetString("error.category", error.Category);
            SetString("error.fully_qualified_id", error.FullyQualifiedId);
            if (error.ScriptLineNumber.HasValue) activity.SetTag("error.script_line_number", error.ScriptLineNumber.Value);
            if (error.ScriptColumnNumber.HasValue) activity.SetTag("error.script_column_number", error.ScriptColumnNumber.Value);
            SetString("error.command_name", error.CommandName);
            SetString("error.stack_trace", error.StackTrace);

            var eventTags = new ActivityTagsCollection();
            if (!string.IsNullOrWhiteSpace(error.Type)) eventTags["exception.type"] = error.Type;
            if (!string.IsNullOrWhiteSpace(error.Message)) eventTags["exception.message"] = error.Message;
            if (!string.IsNullOrWhiteSpace(error.StackTrace)) eventTags["exception.stacktrace"] = error.StackTrace;
            activity.AddEvent(new ActivityEvent("exception", tags: eventTags));
        }

        if (evt.Attributes is { } attributes)
        {
            foreach (var (key, raw) in attributes)
            {
                var value = NormalizeAttributeValue(raw);
                if (value is null) continue;
                activity.SetTag("attr." + key, value);
            }
        }

        var isError = string.Equals(evt.Status, "failed", StringComparison.OrdinalIgnoreCase);
        activity.SetStatus(
            isError ? ActivityStatusCode.Error : ActivityStatusCode.Ok,
            isError ? evt.Error?.Message : null);
    }

    /// <summary>
    /// Unwraps a JSON-sourced attribute value into an OTel-friendly primitive.
    /// Buffered events deserialize their free-form attributes as
    /// <see cref="JsonElement"/>; mirrors <c>ExportService.NormalizeAttributeValue</c>.
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
                return new UriBuilder(uri) { Path = $"{path}/v1/traces".TrimStart('/') }.Uri;
            }
        }

        return uri;
    }

    public void Dispose() => _provider?.Dispose();

    public ValueTask DisposeAsync()
    {
        // ForceFlush then dispose so in-flight spans are pushed on shutdown.
        _provider?.ForceFlush(_flushTimeoutMs);
        _provider?.Dispose();
        _activitySource.Dispose();
        return ValueTask.CompletedTask;
    }
}
