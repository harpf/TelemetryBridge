namespace TelemetryBridge.Core.Models;

public sealed class BridgeConfig
{
    public AgentOptions Agent { get; init; } = new();
    public ServiceOptions Service { get; init; } = new();
    public SignozOptions Signoz { get; init; } = new();
    public BufferOptions Buffer { get; init; } = new();

    public static BridgeConfig Default(string workspaceRoot)
    {
        var bufferPath = Path.Combine(workspaceRoot, "Data", "Buffer");

        return new BridgeConfig
        {
            Buffer = new BufferOptions
            {
                Enabled = true,
                Path = bufferPath,
                MaxSizeMb = 500
            }
        };
    }

    public sealed class AgentOptions
    {
        public string InstanceId { get; init; } = "auto";
        public bool StrictMode { get; init; }
    }

    public sealed class ServiceOptions
    {
        public string Name { get; init; } = "telemetrybridge";
        public string Namespace { get; init; } = "default";
        public string Version { get; init; } = "1.0.0";
        public string Environment { get; init; } = "production";
    }

    public sealed class SignozOptions
    {
        public string Endpoint { get; init; } = "http://localhost:4317";
        public string Protocol { get; init; } = "grpc";
        public int TimeoutSeconds { get; init; } = 10;
        public bool UseTls { get; init; }
        public string? Headers { get; init; }
    }

    public sealed class BufferOptions
    {
        public bool Enabled { get; init; } = true;
        public string Path { get; init; } = "./buffer";
        public int MaxSizeMb { get; init; } = 500;
    }
}
