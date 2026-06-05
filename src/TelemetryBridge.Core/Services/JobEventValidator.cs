using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

/// <summary>Result of validating/normalizing a <see cref="JobEvent"/>.</summary>
public sealed record ValidationOutcome(
    JobEvent Event,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Validates the closed-enum fields (<c>eventType</c>, <c>status</c>,
/// <c>system</c>) against <see cref="EventContract"/>.
///
/// In non-strict mode an invalid value is normalized to a safe contract member
/// (status → <c>unknown</c>, eventType → <c>job</c>, system → <c>manual</c>) and
/// surfaced as a warning, so a bad value never crashes the send path. In strict
/// mode the same value is reported as an error instead and the event is returned
/// unchanged for the caller to reject.
/// </summary>
public static class JobEventValidator
{
    public static ValidationOutcome ValidateAndNormalize(JobEvent evt, bool strict)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        var eventType = Resolve(
            "eventType", evt.EventType, EventContract.EventTypes, "job", strict, warnings, errors);
        var status = Resolve(
            "status", evt.Status, EventContract.Statuses, "unknown", strict, warnings, errors);
        var system = Resolve(
            "system", evt.System, EventContract.Systems, "manual", strict, warnings, errors);

        if (errors.Count > 0)
        {
            // Strict mode: hand the original event back untouched for rejection.
            return new ValidationOutcome(evt, warnings, errors);
        }

        var normalized =
            string.Equals(eventType, evt.EventType, StringComparison.Ordinal) &&
            string.Equals(status, evt.Status, StringComparison.Ordinal) &&
            string.Equals(system, evt.System, StringComparison.Ordinal)
                ? evt
                : evt with { EventType = eventType, Status = status, System = system };

        return new ValidationOutcome(normalized, warnings, errors);
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
