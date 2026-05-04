using System.Text.Json;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string ConfigFileName = "telemetrybridge.config.json";

    public string GetWorkspaceRoot(string? overrideRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return overrideRoot;
        return Path.Combine(Environment.CurrentDirectory, "TelemetryBridge");
    }

    public string GetConfigPath(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        return Path.Combine(GetWorkspaceRoot(), ConfigFileName);
    }

    public BridgeConfig Init(string? path = null)
    {
        var configPath = GetConfigPath(path);
        var workspaceRoot = Path.GetDirectoryName(configPath) ?? ".";
        var config = BridgeConfig.Default(workspaceRoot);

        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(config.Buffer.Path);

        File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));
        return config;
    }

    public BridgeConfig LoadOrCreate(string? path = null)
    {
        var configPath = GetConfigPath(path);
        return File.Exists(configPath) ? Load(configPath) : Init(configPath);
    }

    public BridgeConfig Load(string? path = null)
    {
        var configPath = GetConfigPath(path);
        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<BridgeConfig>(json) ?? new BridgeConfig();
    }

    public IReadOnlyList<string> Validate(BridgeConfig config)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(config.Signoz.Endpoint)) errors.Add("signoz.endpoint is required");
        if (config.Signoz.TimeoutSeconds <= 0) errors.Add("signoz.timeoutSeconds must be > 0");
        if (config.Buffer.MaxSizeMb <= 0) errors.Add("buffer.maxSizeMb must be > 0");
        if (string.IsNullOrWhiteSpace(config.Buffer.Path)) errors.Add("buffer.path is required");
        if (config.Service.TimeoutSeconds <= 0) errors.Add("service.timeoutSeconds must be > 0");
        if (config.Service.TcpPort <= 0) errors.Add("service.tcpPort must be > 0");
        if (string.IsNullOrWhiteSpace(config.Service.HttpEndpoint)) errors.Add("service.httpEndpoint is required");
        return errors;
    }
}
