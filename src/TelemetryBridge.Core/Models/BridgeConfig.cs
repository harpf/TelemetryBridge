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
        var deadLetterPath = Path.Combine(workspaceRoot, "Data", "DeadLetter");

        return new BridgeConfig
        {
            Buffer = new BufferOptions
            {
                Enabled = true,
                Path = bufferPath,
                MaxSizeMb = 500,
                DeadLetterPath = deadLetterPath
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

        /// <summary>Items older than this are dead-lettered instead of retried.</summary>
        public int MaxEventAgeHours { get; init; } = 72;

        /// <summary>How often the retry worker drains the buffer.</summary>
        public int FlushIntervalSeconds { get; init; } = 30;

        /// <summary>Maximum number of items processed per drain pass.</summary>
        public int BatchSize { get; init; } = 50;

        /// <summary>Maximum delivery attempts before an item is dead-lettered.</summary>
        public int MaxAttempts { get; init; } = 10;

        /// <summary>When false, exhausted items are simply dropped rather than parked.</summary>
        public bool DeadLetterEnabled { get; init; } = true;

        /// <summary>Folder that holds items that exhausted their retry budget.</summary>
        public string DeadLetterPath { get; init; } = "./buffer/dead-letter";
    }
}
