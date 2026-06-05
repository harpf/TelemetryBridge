using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Health;
using TelemetryBridge.Service.Hosting;
using TelemetryBridge.Service.Ingest;
using static TelemetryBridge.Service.Tests.TestSupport;

namespace TelemetryBridge.Service.Tests;

/// <summary>
/// End-to-end exercise of the real <see cref="HttpListener"/> server on a
/// loopback port. If the environment can't grant the URL ACL (locked-down CI),
/// the test soft-skips rather than failing spuriously.
/// </summary>
public sealed class HttpIngestServerTests
{
    [Fact]
    public async Task Serves_health_and_accepts_posted_events()
    {
        if (!CanBindLoopback(out var port))
        {
            return; // soft skip: loopback HttpListener not available here
        }

        var dir = NewTempDir();
        var buffer = new BufferService();
        var options = Options(bufferPath: dir, enableIngest: true, listenUrl: $"http://localhost:{port}");
        var server = new HttpIngestServer(
            new JobEventIngestHandler(buffer, options),
            new HealthReporter(buffer, options),
            options,
            NullLogger<HttpIngestServer>.Instance);

        await server.StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var baseUrl = $"http://localhost:{port}";

            // Wait until the listener is accepting.
            await WaitForAsync(() => TryGet(client, $"{baseUrl}/health"), TimeSpan.FromSeconds(5),
                "the /health endpoint should come up");

            // GET /health -> 200 + buffer status
            var health = await client.GetAsync($"{baseUrl}/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using (var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync()))
            {
                Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
            }

            // POST /ingest -> 202 + the event lands in the durable buffer
            var payload = new StringContent(
                "{\"jobName\":\"http-job\",\"status\":\"succeeded\",\"system\":\"powershell\"}",
                Encoding.UTF8, "application/json");
            var ingest = await client.PostAsync($"{baseUrl}/ingest", payload);
            Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);

            var buffered = buffer.List(dir);
            Assert.Single(buffered);
            Assert.Equal("http-job", buffered[0].Event.JobName);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static bool TryGet(HttpClient client, string url)
    {
        try
        {
            using var response = client.GetAsync(url).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanBindLoopback(out int port)
    {
        port = FreeTcpPort();
        try
        {
            using var probe = new HttpListener();
            probe.Prefixes.Add($"http://localhost:{port}/");
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (HttpListenerException)
        {
            return false;
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
