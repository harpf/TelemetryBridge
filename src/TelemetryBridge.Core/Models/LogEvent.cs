using System.Text.Json.Serialization;

namespace TelemetryBridge.Core.Models;

/// <summary>
/// A single log telemetry event. Mirrors the camelCase, contract-style shape of
/// <see cref="JobEvent"/> (eventType, timestamp, correlationId, attributes) and
/// adds the log-specific <c>severity</c> + <c>body</c>. The closed
/// <c>severity</c> enum lives in <see cref="SignalContract"/>.
/// </summary>
public sealed record LogEvent
{
    public string EventType { get; init; } = "log";

    public string LogRecordId { get; init; } = Guid.NewGuid().ToString();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instance { get; init; }

    public string Severity { get; init; } = "info";

    public string Body { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Free-form, contract-allowed attributes (string/number/bool/null).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Attributes { get; init; }
}
