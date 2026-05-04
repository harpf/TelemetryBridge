namespace TelemetryBridge.Core.Models;

public sealed class JobEvent
{
    public string EventType { get; init; } = "job";
    public string JobRunId { get; init; } = Guid.NewGuid().ToString();
    public string System { get; init; } = "powershell";
    public string JobName { get; init; } = string.Empty;
    public string Status { get; init; } = "succeeded";
    public string? RunbookName { get; init; }
    public string? ScriptPath { get; init; }
    public string? HostName { get; init; }
    public string? EnvironmentName { get; init; }
    public double? DurationMs { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
