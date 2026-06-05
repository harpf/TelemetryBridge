using System.Text.Json;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

/// <summary>
/// Verifies that <see cref="LogEvent"/> / <see cref="MetricEvent"/> serialize as
/// camelCase (event-contract style) and that the closed enums (log severity,
/// metric kind) validate/normalize the same way <see cref="JobEvent"/> does.
/// </summary>
public sealed class SignalContractTests
{
    private static LogEvent FullLog() => new()
    {
        EventType = "log",
        LogRecordId = "33333333-3333-3333-3333-333333333333",
        CorrelationId = "corr-1",
        Severity = "error",
        Body = "something broke",
        Timestamp = DateTimeOffset.Parse("2026-06-05T10:00:00Z"),
        Instance = "host-7",
        Attributes = new Dictionary<string, object?>
        {
            ["region"] = "eu-central",
            ["retryCount"] = 3L,
            ["isDryRun"] = true,
            ["note"] = null
        }
    };

    private static MetricEvent FullMetric() => new()
    {
        EventType = "metric",
        MetricId = "44444444-4444-4444-4444-444444444444",
        CorrelationId = "corr-1",
        Name = "job.duration",
        Value = 1234.5,
        Unit = "ms",
        Kind = "gauge",
        Timestamp = DateTimeOffset.Parse("2026-06-05T10:00:00Z"),
        Instance = "host-7",
        Attributes = new Dictionary<string, object?>
        {
            ["region"] = "eu-central"
        }
    };

    // ---- LogEvent round-trip + camelCase --------------------------------

    [Fact]
    public void LogEvent_RoundTrips_AllFields()
    {
        var original = FullLog();

        var json = JsonSerializer.Serialize(original, ConfigService.JsonOptions);
        var copy = JsonSerializer.Deserialize<LogEvent>(json, ConfigService.JsonOptions)!;

        Assert.Equal(original.EventType, copy.EventType);
        Assert.Equal(original.LogRecordId, copy.LogRecordId);
        Assert.Equal(original.CorrelationId, copy.CorrelationId);
        Assert.Equal(original.Severity, copy.Severity);
        Assert.Equal(original.Body, copy.Body);
        Assert.Equal(original.Timestamp, copy.Timestamp);
        Assert.Equal(original.Instance, copy.Instance);
        Assert.NotNull(copy.Attributes);
    }

    [Fact]
    public void LogEvent_Serializes_AsCamelCase()
    {
        var json = JsonSerializer.Serialize(FullLog(), ConfigService.JsonOptions);

        Assert.Contains("\"eventType\"", json);
        Assert.Contains("\"logRecordId\"", json);
        Assert.Contains("\"correlationId\"", json);
        Assert.Contains("\"severity\"", json);
        Assert.Contains("\"body\"", json);
        Assert.Contains("\"timestamp\"", json);
        Assert.DoesNotContain("\"Body\"", json);
    }

    // ---- MetricEvent round-trip + camelCase -----------------------------

    [Fact]
    public void MetricEvent_RoundTrips_AllFields()
    {
        var original = FullMetric();

        var json = JsonSerializer.Serialize(original, ConfigService.JsonOptions);
        var copy = JsonSerializer.Deserialize<MetricEvent>(json, ConfigService.JsonOptions)!;

        Assert.Equal(original.EventType, copy.EventType);
        Assert.Equal(original.MetricId, copy.MetricId);
        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(original.Value, copy.Value);
        Assert.Equal(original.Unit, copy.Unit);
        Assert.Equal(original.Kind, copy.Kind);
        Assert.Equal(original.Timestamp, copy.Timestamp);
    }

    [Fact]
    public void MetricEvent_Serializes_AsCamelCase()
    {
        var json = JsonSerializer.Serialize(FullMetric(), ConfigService.JsonOptions);

        Assert.Contains("\"eventType\"", json);
        Assert.Contains("\"metricId\"", json);
        Assert.Contains("\"name\"", json);
        Assert.Contains("\"value\"", json);
        Assert.Contains("\"unit\"", json);
        Assert.Contains("\"kind\"", json);
        Assert.DoesNotContain("\"Name\"", json);
    }

    // ---- Contract enums --------------------------------------------------

    [Theory]
    [InlineData("trace")]
    [InlineData("debug")]
    [InlineData("info")]
    [InlineData("warn")]
    [InlineData("error")]
    [InlineData("fatal")]
    public void SignalContract_AcceptsValidSeverities(string value) =>
        Assert.True(SignalContract.IsValidSeverity(value));

    [Theory]
    [InlineData("counter")]
    [InlineData("gauge")]
    public void SignalContract_AcceptsValidMetricKinds(string value) =>
        Assert.True(SignalContract.IsValidMetricKind(value));

    [Theory]
    [InlineData("verbose")]
    [InlineData("")]
    [InlineData(null)]
    public void SignalContract_RejectsUnknownSeverities(string? value) =>
        Assert.False(SignalContract.IsValidSeverity(value));

    [Theory]
    [InlineData("histogram")]
    [InlineData("")]
    [InlineData(null)]
    public void SignalContract_RejectsUnknownMetricKinds(string? value) =>
        Assert.False(SignalContract.IsValidMetricKind(value));

    // ---- Log validation / normalization ----------------------------------

    [Fact]
    public void ValidateLog_ValidEvent_ProducesNoWarningsOrErrors()
    {
        var outcome = LogEventValidator.ValidateAndNormalize(FullLog(), strict: false);

        Assert.Empty(outcome.Warnings);
        Assert.Empty(outcome.Errors);
        Assert.False(outcome.HasErrors);
    }

    [Fact]
    public void ValidateLog_InvalidSeverity_NonStrict_NormalizesToInfo_WithWarning()
    {
        var evt = new LogEvent { Body = "hi", Severity = "bogus" };

        var outcome = LogEventValidator.ValidateAndNormalize(evt, strict: false);

        Assert.False(outcome.HasErrors);
        Assert.NotEmpty(outcome.Warnings);
        Assert.Equal("info", outcome.Event.Severity);
    }

    [Fact]
    public void ValidateLog_InvalidSeverity_Strict_ReportsError()
    {
        var evt = new LogEvent { Body = "hi", Severity = "bogus" };

        var outcome = LogEventValidator.ValidateAndNormalize(evt, strict: true);

        Assert.True(outcome.HasErrors);
    }

    [Fact]
    public void ValidateLog_EmptyBody_ReportsError()
    {
        var evt = new LogEvent { Body = "", Severity = "info" };

        var outcome = LogEventValidator.ValidateAndNormalize(evt, strict: false);

        Assert.True(outcome.HasErrors);
    }

    // ---- Metric validation / normalization -------------------------------

    [Fact]
    public void ValidateMetric_ValidEvent_ProducesNoWarningsOrErrors()
    {
        var outcome = MetricEventValidator.ValidateAndNormalize(FullMetric(), strict: false);

        Assert.Empty(outcome.Warnings);
        Assert.Empty(outcome.Errors);
        Assert.False(outcome.HasErrors);
    }

    [Fact]
    public void ValidateMetric_InvalidKind_NonStrict_NormalizesToGauge_WithWarning()
    {
        var evt = new MetricEvent { Name = "m", Value = 1, Kind = "bogus" };

        var outcome = MetricEventValidator.ValidateAndNormalize(evt, strict: false);

        Assert.False(outcome.HasErrors);
        Assert.NotEmpty(outcome.Warnings);
        Assert.Equal("gauge", outcome.Event.Kind);
    }

    [Fact]
    public void ValidateMetric_InvalidKind_Strict_ReportsError()
    {
        var evt = new MetricEvent { Name = "m", Value = 1, Kind = "bogus" };

        var outcome = MetricEventValidator.ValidateAndNormalize(evt, strict: true);

        Assert.True(outcome.HasErrors);
    }

    [Fact]
    public void ValidateMetric_EmptyName_ReportsError()
    {
        var evt = new MetricEvent { Name = "", Value = 1, Kind = "gauge" };

        var outcome = MetricEventValidator.ValidateAndNormalize(evt, strict: false);

        Assert.True(outcome.HasErrors);
    }

    [Fact]
    public void ValidateMetric_NonFiniteValue_ReportsError()
    {
        var evt = new MetricEvent { Name = "m", Value = double.NaN, Kind = "gauge" };

        var outcome = MetricEventValidator.ValidateAndNormalize(evt, strict: false);

        Assert.True(outcome.HasErrors);
    }
}
