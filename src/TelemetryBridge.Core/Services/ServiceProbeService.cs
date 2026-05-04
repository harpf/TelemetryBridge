using System.Net.Http;
using System.Net.Sockets;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

public sealed class ServiceProbeService
{
    public async Task<ServiceProbeResult> ProbeAsync(BridgeConfig.ServiceOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return new ServiceProbeResult(false, "Service probing is disabled in config.", null, null);
        }

        var httpResult = await ProbeHttpAsync(options.HttpEndpoint, options.TimeoutSeconds, cancellationToken);
        var tcpResult = await ProbeTcpAsync(options.TcpHost, options.TcpPort, options.TimeoutSeconds, cancellationToken);

        var success = httpResult.IsSuccess && tcpResult.IsSuccess;
        return new ServiceProbeResult(success, success ? "Local services reachable." : "One or more local service checks failed.", httpResult, tcpResult);
    }

    private static async Task<ProbeCheckResult> ProbeHttpAsync(string endpoint, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) };
            using var response = await client.GetAsync(endpoint, cancellationToken);
            return new ProbeCheckResult(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return new ProbeCheckResult(false, ex.Message);
        }
    }

    private static async Task<ProbeCheckResult> ProbeTcpAsync(string host, int port, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            await client.ConnectAsync(host, port, cts.Token);
            return new ProbeCheckResult(true, $"TCP connection to {host}:{port} succeeded");
        }
        catch (Exception ex)
        {
            return new ProbeCheckResult(false, ex.Message);
        }
    }
}

public sealed record ServiceProbeResult(bool IsSuccess, string Message, ProbeCheckResult? Http, ProbeCheckResult? Tcp);
public sealed record ProbeCheckResult(bool IsSuccess, string Message);
