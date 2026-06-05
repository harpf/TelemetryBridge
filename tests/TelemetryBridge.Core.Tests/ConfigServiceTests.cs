using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tb-config-tests", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "telemetrybridge.config.json");

    [Fact]
    public void InitThenLoad_RoundTripsValues()
    {
        var service = new ConfigService();

        var created = service.Init(ConfigPath);
        var loaded = service.Load(ConfigPath);

        Assert.Equal(created.Signoz.Endpoint, loaded.Signoz.Endpoint);
        Assert.Equal(created.Signoz.Protocol, loaded.Signoz.Protocol);
        Assert.Equal(created.Buffer.Path, loaded.Buffer.Path);
        Assert.Equal(created.Buffer.MaxSizeMb, loaded.Buffer.MaxSizeMb);
    }

    [Fact]
    public void Init_WritesCamelCaseOnDisk()
    {
        var service = new ConfigService();
        service.Init(ConfigPath);

        var json = File.ReadAllText(ConfigPath);

        Assert.Contains("\"signoz\"", json);
        Assert.Contains("\"endpoint\"", json);
        // The PascalCase property names must NOT be on disk.
        Assert.DoesNotContain("\"Signoz\"", json);
        Assert.DoesNotContain("\"Endpoint\"", json);
    }

    [Fact]
    public void Load_BindsCamelCaseConfig_WithoutSilentlyDefaulting()
    {
        // A user copies the camelCase sample; the real endpoint must be honored.
        const string camelCaseJson = """
        {
          "agent": { "instanceId": "host-01", "strictMode": true },
          "signoz": { "endpoint": "http://signoz01.internal:4317", "protocol": "grpc", "timeoutSeconds": 25 },
          "buffer": { "enabled": true, "path": "C:\\data\\buffer", "maxSizeMb": 250 }
        }
        """;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, camelCaseJson);

        var service = new ConfigService();
        var config = service.Load(ConfigPath);

        Assert.Equal("http://signoz01.internal:4317", config.Signoz.Endpoint);
        Assert.Equal(25, config.Signoz.TimeoutSeconds);
        Assert.Equal("host-01", config.Agent.InstanceId);
        Assert.True(config.Agent.StrictMode);
        Assert.Equal("C:\\data\\buffer", config.Buffer.Path);
        Assert.Equal(250, config.Buffer.MaxSizeMb);
    }

    [Fact]
    public void Validate_ReturnsErrorsForInvalidConfig()
    {
        var service = new ConfigService();
        var config = new BridgeConfig
        {
            Signoz = new BridgeConfig.SignozOptions { Endpoint = "", TimeoutSeconds = 0 },
            Buffer = new BridgeConfig.BufferOptions { Path = "", MaxSizeMb = 0 }
        };

        var errors = service.Validate(config);

        Assert.Contains(errors, e => e.Contains("signoz.endpoint"));
        Assert.Contains(errors, e => e.Contains("signoz.timeoutSeconds"));
        Assert.Contains(errors, e => e.Contains("buffer.maxSizeMb"));
        Assert.Contains(errors, e => e.Contains("buffer.path"));
    }

    [Fact]
    public void Validate_ReturnsNoErrorsForDefaultConfig()
    {
        var service = new ConfigService();
        var config = BridgeConfig.Default(_dir);

        var errors = service.Validate(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void DefaultConfig_PopulatesBufferRetryFields()
    {
        var config = BridgeConfig.Default(_dir);

        Assert.True(config.Buffer.BatchSize > 0);
        Assert.True(config.Buffer.FlushIntervalSeconds > 0);
        Assert.True(config.Buffer.MaxEventAgeHours > 0);
        Assert.True(config.Buffer.MaxAttempts > 0);
        Assert.True(config.Buffer.DeadLetterEnabled);
        Assert.False(string.IsNullOrWhiteSpace(config.Buffer.DeadLetterPath));
    }

    [Fact]
    public void Validate_ReturnsErrorsForInvalidBufferRetryFields()
    {
        var service = new ConfigService();
        var config = new BridgeConfig
        {
            Buffer = new BridgeConfig.BufferOptions
            {
                Path = "C:\\data\\buffer",
                MaxSizeMb = 100,
                BatchSize = 0,
                FlushIntervalSeconds = 0,
                MaxEventAgeHours = 0,
                MaxAttempts = 0,
                DeadLetterEnabled = true,
                DeadLetterPath = ""
            }
        };

        var errors = service.Validate(config);

        Assert.Contains(errors, e => e.Contains("buffer.batchSize"));
        Assert.Contains(errors, e => e.Contains("buffer.flushIntervalSeconds"));
        Assert.Contains(errors, e => e.Contains("buffer.maxEventAgeHours"));
        Assert.Contains(errors, e => e.Contains("buffer.maxAttempts"));
        Assert.Contains(errors, e => e.Contains("buffer.deadLetterPath"));
    }

    [Fact]
    public void Load_BindsBufferRetryFields_FromCamelCaseSample()
    {
        const string json = """
        {
          "buffer": {
            "enabled": true,
            "path": "C:\\data\\buffer",
            "maxSizeMb": 500,
            "maxEventAgeHours": 48,
            "flushIntervalSeconds": 15,
            "batchSize": 25,
            "maxAttempts": 7,
            "deadLetterEnabled": true,
            "deadLetterPath": "C:\\data\\dead-letter"
          }
        }
        """;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, json);

        var config = new ConfigService().Load(ConfigPath);

        Assert.Equal(48, config.Buffer.MaxEventAgeHours);
        Assert.Equal(15, config.Buffer.FlushIntervalSeconds);
        Assert.Equal(25, config.Buffer.BatchSize);
        Assert.Equal(7, config.Buffer.MaxAttempts);
        Assert.True(config.Buffer.DeadLetterEnabled);
        Assert.Equal("C:\\data\\dead-letter", config.Buffer.DeadLetterPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
