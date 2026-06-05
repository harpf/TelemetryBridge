using System.Text.Json.Serialization;

namespace TelemetryBridge.Core.Models;

/// <summary>
/// A single job telemetry event. The shape (and camelCase serialization) is
/// aligned with docs/Event-Contract.json, which is the ground-truth schema.
/// </summary>
public sealed record JobEvent
{
    private readonly JobError? _error;

    public string EventType { get; init; } = "job";

    public string JobRunId { get; init; } = Guid.NewGuid().ToString();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentCorrelationId { get; init; }

    public string System { get; init; } = "powershell";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instance { get; init; }

    public string JobName { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptPath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunbookName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentName { get; init; }

    public string Status { get; init; } = "succeeded";

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DurationMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; init; }

    /// <summary>Structured error details, present only for failing runs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobError? Error
    {
        get => _error;
        init => _error = value;
    }

    /// <summary>
    /// Backward-compatibility shim: events buffered before P2 carried a flat
    /// <c>errorMessage</c> string instead of the structured <see cref="Error"/>
    /// object. We accept it on read (folding it into <see cref="Error"/>) but
    /// never write it back out, so re-serialized files match the contract.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage
    {
        get => null;
        init
        {
            if (!string.IsNullOrWhiteSpace(value) && _error is null)
            {
                _error = new JobError { Message = value };
            }
        }
    }

    /// <summary>Free-form, contract-allowed attributes (string/number/bool/null).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Attributes { get; init; }
}
