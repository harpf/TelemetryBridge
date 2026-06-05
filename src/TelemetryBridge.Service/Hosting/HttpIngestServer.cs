using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelemetryBridge.Service.Configuration;
using TelemetryBridge.Service.Health;
using TelemetryBridge.Service.Ingest;

namespace TelemetryBridge.Service.Hosting;

/// <summary>
/// Minimal localhost HTTP surface, hosted on <see cref="HttpListener"/> so the
/// service stays a plain Worker (no ASP.NET framework reference). Started only
/// when <c>agent.enableHttpIngest</c> is set, and bound to a loopback prefix so
/// it is never reachable off-box.
///
/// Routes:
///   POST /ingest (or POST /)  → validate + enqueue a JobEvent, 202 Accepted
///   GET  /health              → 200 with the current buffer status
/// </summary>
public sealed class HttpIngestServer : BackgroundService
{
    private readonly JobEventIngestHandler _ingest;
    private readonly HealthReporter _health;
    private readonly ServiceHostOptions _options;
    private readonly ILogger<HttpIngestServer> _logger;

    public HttpIngestServer(
        JobEventIngestHandler ingest,
        HealthReporter health,
        ServiceHostOptions options,
        ILogger<HttpIngestServer> logger)
    {
        _ingest = ingest;
        _health = health;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableHttpIngest)
        {
            _logger.LogInformation("HTTP ingest disabled (agent.enableHttpIngest=false); endpoint not started.");
            return;
        }

        if (!HttpListener.IsSupported)
        {
            _logger.LogWarning("HttpListener is not supported on this platform; HTTP ingest not started.");
            return;
        }

        var prefix = NormalizePrefix(_options.ListenUrl);
        if (!IsLoopback(prefix))
        {
            _logger.LogError(
                "Refusing to bind HTTP ingest to non-loopback prefix '{Prefix}'. Use http://localhost:<port>/.",
                prefix);
            return;
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "Could not start HTTP ingest on {Prefix}.", prefix);
            return;
        }

        _logger.LogInformation("HTTP ingest listening on {Prefix} (POST /ingest, GET /health).", prefix);

        // Stop the listener when the host is shutting down so the blocking
        // GetContextAsync returns.
        await using var registration = stoppingToken.Register(() =>
        {
            try { listener.Stop(); } catch { /* shutting down */ }
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            // Handle each request without blocking the accept loop.
            _ = Task.Run(() => HandleContextAsync(context, stoppingToken), CancellationToken.None);
        }

        _logger.LogInformation("HTTP ingest stopped.");
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath?.TrimEnd('/');
            var method = context.Request.HttpMethod;

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
            {
                var status = _health.Snapshot();
                await WriteAsync(context, 200, _health.ToHealthJson(status), cancellationToken);
                return;
            }

            var isIngestPath = string.IsNullOrEmpty(path) ||
                               string.Equals(path, "/ingest", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && isIngestPath)
            {
                var result = await _ingest.HandleAsync(context.Request.InputStream, cancellationToken);
                await WriteAsync(context, result.StatusCode, result.Body, cancellationToken);
                return;
            }

            await WriteAsync(context, 404,
                "{\"status\":\"not-found\",\"error\":\"Use POST /ingest or GET /health.\"}", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling HTTP ingest request.");
            try
            {
                await WriteAsync(context, 500,
                    "{\"status\":\"error\",\"error\":\"Internal server error.\"}", cancellationToken);
            }
            catch
            {
                // Connection already gone; nothing more to do.
            }
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, int statusCode, string body, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.OutputStream.Close();
    }

    /// <summary>HttpListener prefixes must end in '/'. Tolerate a missing slash.</summary>
    internal static string NormalizePrefix(string listenUrl)
    {
        var url = string.IsNullOrWhiteSpace(listenUrl) ? ServiceHostOptions.DefaultListenUrl : listenUrl.Trim();
        return url.EndsWith('/') ? url : url + "/";
    }

    /// <summary>True only for loopback prefixes, so we never bind off-box.</summary>
    internal static bool IsLoopback(string prefix)
    {
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri)) return false;

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
