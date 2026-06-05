using TelemetryBridge.Core.Models;
using TelemetryBridge.Service.Configuration;
using TelemetryBridge.Service.Hosting;

namespace TelemetryBridge.Service.Tests;

public sealed class ServiceHostOptionsTests
{
    [Fact]
    public void Reads_agent_listenUrl_and_enableHttpIngest_from_config_json()
    {
        var json = "{\"agent\":{\"enableHttpIngest\":true,\"listenUrl\":\"http://localhost:6001\"," +
                   "\"heartbeatIntervalSeconds\":15}}";

        var opts = ServiceHostOptions.FromJson(json, "c.json", new BridgeConfig());

        Assert.True(opts.EnableHttpIngest);
        Assert.Equal("http://localhost:6001", opts.ListenUrl);
        Assert.Equal(15, opts.HeartbeatIntervalSeconds);
    }

    [Fact]
    public void Falls_back_to_defaults_when_agent_fields_absent()
    {
        var opts = ServiceHostOptions.FromJson("{\"agent\":{}}", "c.json", new BridgeConfig());

        Assert.False(opts.EnableHttpIngest);
        Assert.Equal(ServiceHostOptions.DefaultListenUrl, opts.ListenUrl);
        Assert.Equal(ServiceHostOptions.DefaultHeartbeatIntervalSeconds, opts.HeartbeatIntervalSeconds);
    }

    [Fact]
    public void Environment_overrides_enable_ingest_and_listen_url()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TELEMETRYBRIDGE_ENABLE_HTTP_INGEST"] = "true",
            ["TELEMETRYBRIDGE_LISTEN_URL"] = "http://127.0.0.1:7000"
        };

        var opts = ServiceHostOptions.FromJson("{\"agent\":{\"enableHttpIngest\":false}}", "c.json",
            new BridgeConfig(), env);

        Assert.True(opts.EnableHttpIngest);
        Assert.Equal("http://127.0.0.1:7000", opts.ListenUrl);
    }

    [Fact]
    public void FlushInterval_comes_from_buffer_config_and_clamps_to_one_second()
    {
        var bridge = new BridgeConfig
        {
            Buffer = new BridgeConfig.BufferOptions { FlushIntervalSeconds = 0 }
        };
        var opts = ServiceHostOptions.FromJson("{}", "c.json", bridge);

        Assert.Equal(TimeSpan.FromSeconds(1), opts.FlushInterval);
    }

    [Theory]
    [InlineData("http://localhost:5050/", true)]
    [InlineData("http://127.0.0.1:5050/", true)]
    [InlineData("http://[::1]:5050/", true)]
    [InlineData("http://0.0.0.0:5050/", false)]
    [InlineData("http://example.com:5050/", false)]
    public void Only_loopback_prefixes_are_accepted(string prefix, bool expected)
    {
        Assert.Equal(expected, HttpIngestServer.IsLoopback(prefix));
    }

    [Theory]
    [InlineData("http://localhost:5050", "http://localhost:5050/")]
    [InlineData("http://localhost:5050/", "http://localhost:5050/")]
    public void Prefix_is_normalized_with_a_trailing_slash(string input, string expected)
    {
        Assert.Equal(expected, HttpIngestServer.NormalizePrefix(input));
    }
}
