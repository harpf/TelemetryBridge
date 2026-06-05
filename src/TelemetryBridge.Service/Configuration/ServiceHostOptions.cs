using System.Text.Json;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Service.Configuration;

/// <summary>
/// Everything the host needs to run, resolved once at startup. It composes the
/// shared <see cref="BridgeConfig"/> (buffer / signoz / flush interval) with the
/// host-only knobs that live under <c>agent</c> in the same config file but are
/// not (yet) modelled on <see cref="BridgeConfig.AgentOptions"/> in Core:
/// <c>listenUrl</c>, <c>enableHttpIngest</c> and <c>heartbeatIntervalSeconds</c>.
///
/// Those three are read straight from the raw JSON so the bridge config file
/// stays the single source of truth and we don't have to touch Core (which a
/// sibling workspace owns). If/when Core grows these fields the loader can drop
/// the raw-JSON pass and read them off <see cref="BridgeConfig"/> directly.
/// </summary>
public sealed record ServiceHostOptions
{
    public const string DefaultListenUrl = "http://localhost:5050";
    public const int DefaultHeartbeatIntervalSeconds = 60;

    public required BridgeConfig Bridge { get; init; }
    public required string ConfigPath { get; init; }

    /// <summary>When false the localhost HTTP ingest endpoint is not started.</summary>
    public bool EnableHttpIngest { get; init; }

    /// <summary>Loopback URL the ingest endpoint binds to (localhost only).</summary>
    public string ListenUrl { get; init; } = DefaultListenUrl;

    public int HeartbeatIntervalSeconds { get; init; } = DefaultHeartbeatIntervalSeconds;

    /// <summary>How often the drain worker runs a buffer pass.</summary>
    public TimeSpan FlushInterval =>
        TimeSpan.FromSeconds(Math.Max(1, Bridge.Buffer.FlushIntervalSeconds));

    public TimeSpan HeartbeatInterval =>
        TimeSpan.FromSeconds(Math.Max(1, HeartbeatIntervalSeconds));

    /// <summary>
    /// Resolves the config path (override → <c>TELEMETRYBRIDGE_CONFIG</c> env →
    /// <see cref="ConfigService"/> default), loads (or creates) the bridge
    /// config, and layers the host-only agent fields on top.
    /// </summary>
    public static ServiceHostOptions Load(
        ConfigService configService,
        string? configPathOverride = null,
        IDictionary<string, string?>? environment = null)
    {
        environment ??= ReadProcessEnvironment();
        var configPath = ResolveConfigPath(configService, configPathOverride, environment);

        // LoadOrCreate so a fresh service install bootstraps a default config
        // (and its data folders) instead of crashing on first run.
        var bridge = configService.LoadOrCreate(configPath);

        var json = File.Exists(configPath) ? File.ReadAllText(configPath) : "{}";
        return FromJson(json, configPath, bridge, environment);
    }

    /// <summary>
    /// Pure overload used by tests: builds the options from already-parsed
    /// config plus the raw JSON that carries the host-only agent fields.
    /// </summary>
    public static ServiceHostOptions FromJson(
        string json,
        string configPath,
        BridgeConfig bridge,
        IDictionary<string, string?>? environment = null)
    {
        environment ??= new Dictionary<string, string?>();

        var enableHttpIngest = false;
        var listenUrl = DefaultListenUrl;
        var heartbeat = DefaultHeartbeatIntervalSeconds;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("agent", out var agent) &&
                agent.ValueKind == JsonValueKind.Object)
            {
                if (TryGetCaseInsensitive(agent, "enableHttpIngest", out var ingestEl) &&
                    (ingestEl.ValueKind == JsonValueKind.True || ingestEl.ValueKind == JsonValueKind.False))
                {
                    enableHttpIngest = ingestEl.GetBoolean();
                }

                if (TryGetCaseInsensitive(agent, "listenUrl", out var urlEl) &&
                    urlEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(urlEl.GetString()))
                {
                    listenUrl = urlEl.GetString()!;
                }

                if (TryGetCaseInsensitive(agent, "heartbeatIntervalSeconds", out var hbEl) &&
                    hbEl.ValueKind == JsonValueKind.Number &&
                    hbEl.TryGetInt32(out var hb) && hb > 0)
                {
                    heartbeat = hb;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON: fall back to defaults. The bridge config itself
            // was already parsed by ConfigService, so we don't double-report.
        }

        // Environment overrides win so operators can flip ingest on/off or
        // repoint the listener without editing the config file.
        if (environment.TryGetValue("TELEMETRYBRIDGE_ENABLE_HTTP_INGEST", out var envIngest) &&
            bool.TryParse(envIngest, out var parsedIngest))
        {
            enableHttpIngest = parsedIngest;
        }

        if (environment.TryGetValue("TELEMETRYBRIDGE_LISTEN_URL", out var envUrl) &&
            !string.IsNullOrWhiteSpace(envUrl))
        {
            listenUrl = envUrl!;
        }

        return new ServiceHostOptions
        {
            Bridge = bridge,
            ConfigPath = configPath,
            EnableHttpIngest = enableHttpIngest,
            ListenUrl = listenUrl,
            HeartbeatIntervalSeconds = heartbeat
        };
    }

    private static string ResolveConfigPath(
        ConfigService configService,
        string? configPathOverride,
        IDictionary<string, string?> environment)
    {
        if (!string.IsNullOrWhiteSpace(configPathOverride)) return configPathOverride!;
        if (environment.TryGetValue("TELEMETRYBRIDGE_CONFIG", out var envPath) &&
            !string.IsNullOrWhiteSpace(envPath))
        {
            return envPath!;
        }
        return configService.GetConfigPath();
    }

    private static bool TryGetCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static Dictionary<string, string?> ReadProcessEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[]
                 {
                     "TELEMETRYBRIDGE_CONFIG",
                     "TELEMETRYBRIDGE_ENABLE_HTTP_INGEST",
                     "TELEMETRYBRIDGE_LISTEN_URL"
                 })
        {
            result[key] = Environment.GetEnvironmentVariable(key);
        }
        return result;
    }
}
