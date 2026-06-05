using System.Net;
using System.Net.Sockets;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

/// <summary>
/// Covers <see cref="DiagnosticsService"/>: the structured probe result shape,
/// the DNS/TCP probes against localhost, the doctor aggregation verdict (with
/// injected results, no real network) and the config redaction used by the
/// <c>collect</c> support bundle.
/// </summary>
public sealed class DiagnosticsServiceTests
{
    // ---- Probe result shape ----------------------------------------------

    [Fact]
    public void ProbeResult_Pass_HasExpectedShape()
    {
        var result = ProbeResult.Pass("dns", "resolved 1 address", 12.3);

        Assert.Equal("dns", result.Name);
        Assert.True(result.Ok);
        Assert.Equal("resolved 1 address", result.Detail);
        Assert.Equal(12.3, result.DurationMs);
        Assert.True(result.Critical);
    }

    [Fact]
    public void ProbeResult_Fail_IsNotOk()
    {
        var result = ProbeResult.Fail("port", "connection refused", 5);

        Assert.False(result.Ok);
        Assert.Equal("connection refused", result.Detail);
    }

    // ---- DNS / TCP probes against localhost ------------------------------

    [Fact]
    public async Task ResolveDnsAsync_Localhost_Succeeds()
    {
        var result = await new DiagnosticsService().ResolveDnsAsync("localhost");

        Assert.Equal("dns", result.Name);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task ResolveDnsAsync_BogusHost_Fails()
    {
        var result = await new DiagnosticsService()
            .ResolveDnsAsync("nonexistent.invalid.tld.example");

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task CheckTcpAsync_OpenPort_Succeeds()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var result = await new DiagnosticsService().CheckTcpAsync("localhost", port);

            Assert.Equal("port", result.Name);
            Assert.True(result.Ok);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CheckTcpAsync_ClosedPort_Fails()
    {
        // Grab an ephemeral port, then release it so the connect is refused.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await new DiagnosticsService().CheckTcpAsync("localhost", port);

        Assert.False(result.Ok);
    }

    // ---- Doctor aggregation verdict --------------------------------------

    [Fact]
    public void BuildReport_AllCriticalOk_VerdictOk_ExitZero()
    {
        var results = new[]
        {
            ProbeResult.Pass("dns", "ok", 1),
            ProbeResult.Pass("port", "ok", 1)
        };

        var report = DiagnosticsService.BuildReport(results);

        Assert.True(report.Ok);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void BuildReport_CriticalFailure_VerdictFail_ExitNonZero()
    {
        var results = new[]
        {
            ProbeResult.Pass("dns", "ok", 1),
            ProbeResult.Fail("otlp", "refused", 1)
        };

        var report = DiagnosticsService.BuildReport(results);

        Assert.False(report.Ok);
        Assert.NotEqual(0, report.ExitCode);
    }

    [Fact]
    public void BuildReport_NonCriticalFailure_StaysOk()
    {
        var results = new[]
        {
            ProbeResult.Pass("dns", "ok", 1),
            ProbeResult.Fail("tls", "no tls", 1, critical: false)
        };

        var report = DiagnosticsService.BuildReport(results);

        Assert.True(report.Ok);
        Assert.Equal(0, report.ExitCode);
    }

    // ---- OTLP probe early-exit (no network) ------------------------------

    [Fact]
    public async Task CheckOtlpAsync_MissingEndpoint_Fails()
    {
        var config = new BridgeConfig { Signoz = new BridgeConfig.SignozOptions { Endpoint = "" } };

        var result = await new DiagnosticsService().CheckOtlpAsync(config);

        Assert.Equal("otlp", result.Name);
        Assert.False(result.Ok);
    }

    // ---- Config redaction for the support bundle -------------------------

    [Fact]
    public void RedactConfigJson_RemovesSignozHeaders()
    {
        var config = new BridgeConfig
        {
            Signoz = new BridgeConfig.SignozOptions
            {
                Endpoint = "http://localhost:4317",
                Headers = "signoz-ingestion-key=SUPER_SECRET_VALUE"
            }
        };

        var json = DiagnosticsService.RedactConfigJson(config);

        Assert.DoesNotContain("SUPER_SECRET_VALUE", json);
        Assert.Contains("REDACTED", json);
        // The non-secret config is still present.
        Assert.Contains("http://localhost:4317", json);
    }

    [Fact]
    public void ParseHostPort_ExtractsHostAndPort()
    {
        var (host, port) = DiagnosticsService.ParseHostPort("http://signoz.example:4318");

        Assert.Equal("signoz.example", host);
        Assert.Equal(4318, port);
    }
}
