using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

/// <summary>Result of validating/normalizing a <see cref="MetricEvent"/>.</summary>
public sealed record MetricValidationOutcome(
    MetricEvent Event,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Validates a <see cref="MetricEvent"/> against <see cref="SignalContract"/>.
///
/// In non-strict mode an invalid <c>kind</c> is normalized to <c>gauge</c> and
/// surfaced as a warning; in strict mode it is reported as an error. An empty
/// <c>name</c> and a non-finite <c>value</c> are always errors. Mirrors
/// <see cref="JobEventValidator"/>.
/// </summary>
public static class MetricEventValidator
{
    public static MetricValidationOutcome ValidateAndNormalize(MetricEvent evt, bool strict)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        var kind = Resolve(
            "kind", evt.Kind, SignalContract.MetricKinds, "gauge", strict, warnings, errors);

        if (string.IsNullOrWhiteSpace(evt.Name))
        {
            errors.Add("name is required.");
        }

        if (double.IsNaN(evt.Value) || double.IsInfinity(evt.Value))
        {
            errors.Add($"value must be a finite number (was '{evt.Value}').");
        }

        if (errors.Count > 0)
        {
            return new MetricValidationOutcome(evt, warnings, errors);
        }

        var normalized = string.Equals(kind, evt.Kind, StringComparison.Ordinal)
            ? evt
            : evt with { Kind = kind };

        return new MetricValidationOutcome(normalized, warnings, errors);
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
