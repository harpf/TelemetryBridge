using System.Text.Json;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string GetConfigPath(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        return Path.Combine(AppContext.BaseDirectory, "telemetrybridge.config.json");
    }

    public BridgeConfig Init(string? path = null)
    {
        var configPath = GetConfigPath(path);
        var config = BridgeConfig.Default();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? ".");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));
        return config;
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
        return errors;
    }
}
