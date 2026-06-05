namespace TelemetryBridge.Core.Models;

/// <summary>
/// The closed enums for the log/metric signals: the <c>severity</c> of a
/// <see cref="LogEvent"/> and the <c>kind</c> of a <see cref="MetricEvent"/>.
/// Validation is case-sensitive against the canonical (lowercase) values, in the
/// same spirit as <see cref="EventContract"/> for job events.
/// </summary>
public static class SignalContract
{
    public static readonly IReadOnlyList<string> LogSeverities =
        new[] { "trace", "debug", "info", "warn", "error", "fatal" };

    public static readonly IReadOnlyList<string> MetricKinds =
        new[] { "counter", "gauge" };

    private static readonly HashSet<string> SeveritySet = new(LogSeverities, StringComparer.Ordinal);
    private static readonly HashSet<string> KindSet = new(MetricKinds, StringComparer.Ordinal);

    public static bool IsValidSeverity(string? value) => value is not null && SeveritySet.Contains(value);

    public static bool IsValidMetricKind(string? value) => value is not null && KindSet.Contains(value);

    public static string? CanonicalizeSeverity(string? value) =>
        EventContract.Canonicalize(value, LogSeverities);

    public static string? CanonicalizeMetricKind(string? value) =>
        EventContract.Canonicalize(value, MetricKinds);
}
