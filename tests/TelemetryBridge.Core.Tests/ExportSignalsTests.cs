using System.Text.Json;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

/// <summary>
/// Covers the additive log/metric surface of <see cref="ExportService"/>: the
/// pure mapping methods (mirroring <c>BuildSpanMapping</c>) and the best-effort
/// send methods' early-exit behavior (no network required).
/// </summary>
public sealed class ExportSignalsTests
{
    private static LogEvent SampleLog() => new()
    {
        EventType = "log",
        LogRecordId = "33333333-3333-3333-3333-333333333333",
        CorrelationId = "corr-1",
        Instance = "host-7",
        Severity = "error",
        Body = "something broke",
        Timestamp = DateTimeOffset.Parse("2026-06-05T10:00:00Z"),
        Attributes = new Dictionary<string, object?>
        {
            ["region"] = "eu-central",
            ["retryCount"] = 3L,
            ["note"] = null
        }
    };

    private static MetricEvent SampleMetric() => new()
    {
        EventType = "metric",
        MetricId = "44444444-4444-4444-4444-444444444444",
        CorrelationId = "corr-1",
        Instance = "host-7",
        Name = "job.duration",
        Value = 1234.5,
        Unit = "ms",
        Kind = "counter",
        Timestamp = DateTimeOffset.Parse("2026-06-05T10:00:00Z"),
        Attributes = new Dictionary<string, object?>
        {
            ["region"] = "eu-central"
        }
    };

    // ---- Log mapping -----------------------------------------------------

    [Fact]
    public void BuildLogMapping_ProjectsBodyAndSeverity()
    {
        var mapping = ExportService.BuildLogMapping(SampleLog());

        Assert.Equal("something broke", mapping.Body);
        Assert.Equal("error", mapping.Severity);
    }

    [Fact]
    public void BuildLogMapping_ProjectsContractFieldsAndAttributes_AsTags()
    {
        var mapping = ExportService.BuildLogMapping(SampleLog());

        Assert.Equal("log", mapping.Attributes["event.type"]);
        Assert.Equal("corr-1", mapping.Attributes["log.correlation_id"]);
        Assert.Equal("host-7", mapping.Attributes["log.instance"]);
        Assert.Equal("eu-central", mapping.Attributes["attr.region"]);
        Assert.Equal(3L, Convert.ToInt64(mapping.Attributes["attr.retryCount"]));
        // Null-valued attributes are skipped rather than emitted as empty tags.
        Assert.False(mapping.Attributes.ContainsKey("attr.note"));
    }

    [Fact]
    public void BuildLogMapping_NormalizesAttributes_AfterJsonRoundTrip()
    {
        var json = JsonSerializer.Serialize(SampleLog(), ConfigService.JsonOptions);
        var fromDisk = JsonSerializer.Deserialize<LogEvent>(json, ConfigService.JsonOptions)!;

        var mapping = ExportService.BuildLogMapping(fromDisk);

        Assert.Equal("eu-central", mapping.Attributes["attr.region"]);
        Assert.Equal(3L, Convert.ToInt64(mapping.Attributes["attr.retryCount"]));
    }

    // ---- Metric mapping --------------------------------------------------

    [Fact]
    public void BuildMetricMapping_ProjectsCoreFields()
    {
        var mapping = ExportService.BuildMetricMapping(SampleMetric());

        Assert.Equal("job.duration", mapping.Name);
        Assert.Equal(1234.5, mapping.Value);
        Assert.Equal("ms", mapping.Unit);
        Assert.Equal("counter", mapping.Kind);
    }

    [Fact]
    public void BuildMetricMapping_ProjectsContractFieldsAndAttributes_AsTags()
    {
        var mapping = ExportService.BuildMetricMapping(SampleMetric());

        Assert.Equal("metric", mapping.Attributes["event.type"]);
        Assert.Equal("corr-1", mapping.Attributes["metric.correlation_id"]);
        Assert.Equal("host-7", mapping.Attributes["metric.instance"]);
        Assert.Equal("eu-central", mapping.Attributes["attr.region"]);
    }

    // ---- Best-effort send early-exit (no network) ------------------------

    [Fact]
    public async Task TrySendLogAsync_MissingEndpoint_Fails()
    {
        var config = new BridgeConfig { Signoz = new BridgeConfig.SignozOptions { Endpoint = "" } };

        var result = await new ExportService().TrySendLogAsync(SampleLog(), config, verbose: false);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task TrySendMetricAsync_UnsupportedProtocol_IsSkipped()
    {
        var config = new BridgeConfig
        {
            Signoz = new BridgeConfig.SignozOptions { Endpoint = "http://localhost:4317", Protocol = "carrier-pigeon" }
        };

        var result = await new ExportService().TrySendMetricAsync(SampleMetric(), config, verbose: false);

        Assert.True(result.IsSkipped);
    }
}
