using TelemetryBridge.Core.Cli;

namespace TelemetryBridge.Core.Tests;

/// <summary>
/// Argument-layer tests for the new <c>send log</c> / <c>send metric</c>
/// subcommands, mirroring the existing <c>send job</c> coverage.
/// </summary>
public sealed class SendSignalArgsTests
{
    // ---- send log --------------------------------------------------------

    [Fact]
    public void SendLogArgs_Parse_BuildsEventFromAllFlags()
    {
        string[] args =
        {
            "--message", "disk almost full",
            "--severity", "warn",
            "--correlation-id", "c1",
            "--instance", "host-1",
            "--attr", "disk=C",
            "--attr", "percentFull=92"
        };

        var parsed = SendLogArgs.Parse(args);

        Assert.Empty(parsed.Errors);
        var evt = parsed.Event!;
        Assert.Equal("disk almost full", evt.Body);
        Assert.Equal("warn", evt.Severity);
        Assert.Equal("c1", evt.CorrelationId);
        Assert.Equal("host-1", evt.Instance);
        Assert.Equal("C", evt.Attributes!["disk"]);
        Assert.Equal(92L, evt.Attributes!["percentFull"]);
    }

    [Fact]
    public void SendLogArgs_Parse_DefaultsSeverityToInfo()
    {
        var parsed = SendLogArgs.Parse(new[] { "--message", "hello" });

        Assert.Empty(parsed.Errors);
        Assert.Equal("info", parsed.Event!.Severity);
    }

    [Fact]
    public void SendLogArgs_Parse_MissingMessage_ReportsError()
    {
        var parsed = SendLogArgs.Parse(new[] { "--severity", "info" });

        Assert.NotEmpty(parsed.Errors);
        Assert.Null(parsed.Event);
    }

    // ---- send metric -----------------------------------------------------

    [Fact]
    public void SendMetricArgs_Parse_BuildsEventFromAllFlags()
    {
        string[] args =
        {
            "--name", "queue.depth",
            "--value", "42.5",
            "--unit", "items",
            "--kind", "gauge",
            "--correlation-id", "c1",
            "--attr", "queue=ingest"
        };

        var parsed = SendMetricArgs.Parse(args);

        Assert.Empty(parsed.Errors);
        var evt = parsed.Event!;
        Assert.Equal("queue.depth", evt.Name);
        Assert.Equal(42.5, evt.Value);
        Assert.Equal("items", evt.Unit);
        Assert.Equal("gauge", evt.Kind);
        Assert.Equal("c1", evt.CorrelationId);
        Assert.Equal("ingest", evt.Attributes!["queue"]);
    }

    [Fact]
    public void SendMetricArgs_Parse_DefaultsKindToGauge()
    {
        var parsed = SendMetricArgs.Parse(new[] { "--name", "m", "--value", "1" });

        Assert.Empty(parsed.Errors);
        Assert.Equal("gauge", parsed.Event!.Kind);
    }

    [Fact]
    public void SendMetricArgs_Parse_MissingName_ReportsError()
    {
        var parsed = SendMetricArgs.Parse(new[] { "--value", "1" });

        Assert.NotEmpty(parsed.Errors);
        Assert.Null(parsed.Event);
    }

    [Fact]
    public void SendMetricArgs_Parse_MissingValue_ReportsError()
    {
        var parsed = SendMetricArgs.Parse(new[] { "--name", "m" });

        Assert.NotEmpty(parsed.Errors);
        Assert.Null(parsed.Event);
    }

    [Fact]
    public void SendMetricArgs_Parse_InvalidValue_ReportsError()
    {
        var parsed = SendMetricArgs.Parse(new[] { "--name", "m", "--value", "notanumber" });

        Assert.NotEmpty(parsed.Errors);
        Assert.Null(parsed.Event);
    }
}
