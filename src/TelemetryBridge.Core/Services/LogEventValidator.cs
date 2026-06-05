using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

/// <summary>Result of validating/normalizing a <see cref="LogEvent"/>.</summary>
public sealed record LogValidationOutcome(
    LogEvent Event,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Validates a <see cref="LogEvent"/> against <see cref="SignalContract"/>.
///
/// In non-strict mode an invalid <c>severity</c> is normalized to <c>info</c>
/// and surfaced as a warning; in strict mode it is reported as an error and the
/// event returned unchanged. An empty <c>body</c> is always an error. Mirrors
/// <see cref="JobEventValidator"/>.
/// </summary>
public static class LogEventValidator
{
    public static LogValidationOutcome ValidateAndNormalize(LogEvent evt, bool strict)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        var severity = Resolve(
            "severity", evt.Severity, SignalContract.LogSeverities, "info", strict, warnings, errors);

        if (string.IsNullOrWhiteSpace(evt.Body))
        {
            errors.Add("body is required.");
        }

        if (errors.Count > 0)
        {
            return new LogValidationOutcome(evt, warnings, errors);
        }

        var normalized = string.Equals(severity, evt.Severity, StringComparison.Ordinal)
            ? evt
            : evt with { Severity = severity };

        return new LogValidationOutcome(normalized, warnings, errors);
    }

    private static string Resolve(
        string field,
        string? original,
        IReadOnlyList<string> allowed,
        string fallback,
        bool strict,
        List<string> warnings,
        List<string> errors)
    {
        var canonical = EventContract.Canonicalize(original, allowed);
        if (canonical is not null) return canonical;

        var allowedList = string.Join(", ", allowed);
        if (strict)
        {
            errors.Add($"Invalid {field} '{original}'. Allowed: {allowedList}.");
            return original ?? fallback;
        }

        warnings.Add($"Invalid {field} '{original}', normalized to '{fallback}'. Allowed: {allowedList}.");
        return fallback;
    }
}
