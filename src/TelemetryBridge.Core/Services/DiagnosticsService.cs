using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

/// <summary>
/// The structured outcome of a single diagnostic probe.
/// </summary>
public sealed record ProbeResult
{
    public required string Name { get; init; }
    public required bool Ok { get; init; }
    public string Detail { get; init; } = string.Empty;
    public double DurationMs { get; init; }

    /// <summary>
    /// When true (the default) a failure of this probe fails the overall
    /// <c>doctor</c> verdict. Informational checks (e.g. an optional TLS probe)
    /// set this to false so they are reported but never break the exit code.
    /// </summary>
    public bool Critical { get; init; } = true;

    public static ProbeResult Pass(string name, string detail, double durationMs, bool critical = true) =>
        new() { Name = name, Ok = true, Detail = detail, DurationMs = durationMs, Critical = critical };

    public static ProbeResult Fail(string name, string detail, double durationMs, bool critical = true) =>
        new() { Name = name, Ok = false, Detail = detail, DurationMs = durationMs, Critical = critical };
}

/// <summary>
/// Aggregated outcome of a <c>doctor</c> run: every probe result plus the
/// overall verdict and the process exit code it implies.
/// </summary>
public sealed record DoctorReport
{
    public required IReadOnlyList<ProbeResult> Results { get; init; }

    /// <summary>True when no <see cref="ProbeResult.Critical"/> probe failed.</summary>
    public required bool Ok { get; init; }

    /// <summary>0 when <see cref="Ok"/>, otherwise non-zero.</summary>
    public int ExitCode => Ok ? 0 : 1;
}

/// <summary>
/// Connectivity and configuration diagnostics for the bridge. Each probe is
/// self-timing and returns a structured <see cref="ProbeResult"/> instead of
/// throwing, so the CLI can render an aggregated report. The aggregation and
/// redaction helpers are static and pure so the verdict logic and secret
/// handling are unit-testable without touching the network.
/// </summary>
public sealed class DiagnosticsService
{
    private readonly ExportService _exportService;

    public DiagnosticsService(ExportService? exportService = null)
    {
        _exportService = exportService ?? new ExportService();
    }

    /// <summary>Resolves <paramref name="host"/> via DNS.</summary>
    public async Task<ProbeResult> ResolveDnsAsync(string host, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);
            sw.Stop();
            return addresses.Length > 0
                ? ProbeResult.Pass("dns", $"resolved {addresses.Length} address(es): {string.Join(", ", addresses.Take(4))}", sw.Elapsed.TotalMilliseconds)
                : ProbeResult.Fail("dns", $"'{host}' resolved to no addresses", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ProbeResult.Fail("dns", $"could not resolve '{host}': {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Opens a TCP connection to <paramref name="host"/>:<paramref name="port"/>.</summary>
    public async Task<ProbeResult> CheckTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, timeout.Token);
            sw.Stop();
            return ProbeResult.Pass("port", $"connected to {host}:{port}", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ProbeResult.Fail("port", $"cannot connect to {host}:{port}: {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Performs a TLS handshake against <paramref name="host"/>:<paramref name="port"/>.
    /// Marked non-critical: a plain-HTTP collector is a valid deployment, so a
    /// failed handshake is informational rather than fatal.
    /// </summary>
    public async Task<ProbeResult> CheckTlsAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, timeout.Token);

            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
            sw.Stop();

            var protocol = ssl.SslProtocol;
            return ProbeResult.Pass("tls", $"handshake ok ({protocol})", sw.Elapsed.TotalMilliseconds, critical: false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ProbeResult.Fail("tls", $"handshake failed for {host}:{port}: {ex.Message}", sw.Elapsed.TotalMilliseconds, critical: false);
        }
    }

    /// <summary>
    /// OTLP reachability: attempts a single minimal trace export to the
    /// configured endpoint and reports whether it was delivered.
    /// </summary>
    public async Task<ProbeResult> CheckOtlpAsync(BridgeConfig config, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var probe = new JobEvent
        {
            JobName = "telemetrybridge.diagnostics.otlp",
            System = "manual",
            Status = "succeeded",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow
        };

        try
        {
            var result = await _exportService.TrySendAsync(probe, config, verbose: false, cancellationToken);
            sw.Stop();
            if (result.IsSuccess)
            {
                return ProbeResult.Pass("otlp", result.Message, sw.Elapsed.TotalMilliseconds);
            }
            // A skipped export (e.g. unsupported protocol) is a config problem,
            // not unreachable network, but still a failed reachability check.
            return ProbeResult.Fail("otlp", result.Message, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ProbeResult.Fail("otlp", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Aggregates probe results into an overall verdict: the run is OK when no
    /// critical probe failed. Pure so the verdict logic can be tested with
    /// injected results and no network.
    /// </summary>
    public static DoctorReport BuildReport(IReadOnlyList<ProbeResult> results)
    {
        var ok = results.Where(r => r.Critical).All(r => r.Ok);
        return new DoctorReport { Results = results, Ok = ok };
    }

    /// <summary>
    /// Splits an OTLP endpoint into host + port, defaulting the port from the
    /// scheme when the URL omits it.
    /// </summary>
    public static (string Host, int Port) ParseHostPort(string endpoint)
    {
        var uri = new Uri(endpoint);
        var port = uri.Port;
        if (port < 0)
        {
            port = uri.Scheme == Uri.UriSchemeHttps ? 443 : 4317;
        }
        return (uri.Host, port);
    }

    /// <summary>
    /// Serializes <paramref name="config"/> to camelCase JSON with the SigNoz
    /// <c>headers</c> (which may carry an ingestion key) redacted, for the
    /// <c>collect</c> support bundle.
    /// </summary>
    public static string RedactConfigJson(BridgeConfig config)
    {
        var node = JsonSerializer.SerializeToNode(config, ConfigService.JsonOptions);
        if (node is JsonObject root && root["signoz"] is JsonObject signoz)
        {
            if (signoz["headers"] is not null && signoz["headers"]!.GetValueKind() != JsonValueKind.Null)
            {
                signoz["headers"] = "***REDACTED***";
            }
        }
        return node?.ToJsonString(ConfigService.JsonOptions) ?? "{}";
    }
}
