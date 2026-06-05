using System.Text.Json.Serialization;

namespace TelemetryBridge.Core.Models;

/// <summary>
/// A single metric telemetry event. Mirrors the camelCase, contract-style shape
/// of <see cref="JobEvent"/> (eventType, timestamp, correlationId, attributes)
/// and adds the metric-specific <c>name</c>, <c>value</c>, <c>unit</c> and
/// <c>kind</c>. The closed <c>kind</c> enum lives in <see cref="SignalContract"/>.
/// </summary>
public sealed record MetricEvent
{
    public string EventType { get; init; } = "metric";

    public string MetricId { get; init; } = Guid.NewGuid().ToString();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instance { get; init; }

    public string Name { get; init; } = string.Empty;

    public double Value { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    /// <summary>One of <see cref="SignalContract.MetricKinds"/> (counter|gauge).</summary>
    public string Kind { get; init; } = "gauge";

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Free-form, contract-allowed attributes (string/number/bool/null).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Attributes { get; init; }
}
