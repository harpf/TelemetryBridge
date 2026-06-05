namespace TelemetryBridge.Core.Models;

/// <summary>
/// The closed enums defined by docs/Event-Contract.json for the
/// <c>eventType</c>, <c>status</c> and <c>system</c> fields. Validation is
/// case-sensitive against the canonical (lowercase) contract values.
/// </summary>
public static class EventContract
{
    public static readonly IReadOnlyList<string> EventTypes =
        new[] { "job", "job-start", "job-end" };

    public static readonly IReadOnlyList<string> Statuses = new[]
    {
        "started", "succeeded", "failed", "warning",
        "skipped", "cancelled", "timeout", "interrupted", "unknown"
    };

    public static readonly IReadOnlyList<string> Systems =
        new[] { "powershell", "scriptrunner", "simego-dss", "ouvvi", "manual" };

    private static readonly HashSet<string> EventTypeSet = new(EventTypes, StringComparer.Ordinal);
    private static readonly HashSet<string> StatusSet = new(Statuses, StringComparer.Ordinal);
    private static readonly HashSet<string> SystemSet = new(Systems, StringComparer.Ordinal);

    public static bool IsValidEventType(string? value) => value is not null && EventTypeSet.Contains(value);

    public static bool IsValidStatus(string? value) => value is not null && StatusSet.Contains(value);

    public static bool IsValidSystem(string? value) => value is not null && SystemSet.Contains(value);

    /// <summary>
    /// Returns the canonical contract value matching <paramref name="value"/>
    /// case-insensitively, or null if it is not a member of <paramref name="allowed"/>.
    /// </summary>
    public static string? Canonicalize(string? value, IReadOnlyList<string> allowed)
    {
        if (value is null) return null;
        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return null;
    }
}
