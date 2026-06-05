using System.Text.Json;
using System.Text.Json.Serialization;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Configuration;

namespace TelemetryBridge.Service.Health;

/// <summary>A point-in-time snapshot of the durable buffer for /health + heartbeat.</summary>
public sealed record BufferStatus
{
    public required int Pending { get; init; }
    public required int Retrying { get; init; }
    public required int DeadLetter { get; init; }
    public required long TotalBytes { get; init; }

    public int Total => Pending + Retrying + DeadLetter;
}

/// <summary>
/// Reads the buffer + dead-letter folders and reports counts/size. Shared by the
/// <c>/health</c> endpoint and the heartbeat log so both tell the same story.
/// </summary>
public sealed class HealthReporter
{
    private static readonly JsonSerializerOptions HealthJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly BufferService _buffer;
    private readonly ServiceHostOptions _options;

    public HealthReporter(BufferService buffer, ServiceHostOptions options)
    {
        _buffer = buffer;
        _options = options;
    }

    public BufferStatus Snapshot()
    {
        var bufferPath = _options.Bridge.Buffer.Path;
        var deadLetterPath = _options.Bridge.Buffer.DeadLetterPath;

        var active = _buffer.List(bufferPath);
        var dead = _buffer.List(deadLetterPath, deadLetter: true);

        return new BufferStatus
        {
            Pending = active.Count(i => i.State == BufferItemState.Pending),
            Retrying = active.Count(i => i.State == BufferItemState.Retrying),
            DeadLetter = dead.Count,
            TotalBytes = active.Sum(i => i.SizeBytes) + dead.Sum(i => i.SizeBytes)
        };
    }

    /// <summary>Builds the JSON body returned by <c>GET /health</c>.</summary>
    public string ToHealthJson(BufferStatus status) =>
        JsonSerializer.Serialize(new
        {
            status = "ok",
            buffer = new
            {
                pending = status.Pending,
                retrying = status.Retrying,
                deadLetter = status.DeadLetter,
                totalBytes = status.TotalBytes
            },
            bufferPath = _options.Bridge.Buffer.Path,
            httpIngest = _options.EnableHttpIngest
        }, HealthJsonOptions);
}
