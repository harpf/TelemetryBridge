using System.Globalization;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Cli;

/// <summary>Outcome of parsing the <c>send metric</c> argument list.</summary>
public sealed record ParsedMetricEvent(MetricEvent? Event, IReadOnlyList<string> Errors);

/// <summary>
/// Parses the flags for the <c>send metric</c> command into a
/// <see cref="MetricEvent"/>. Kept in Core so the argument layer is
/// unit-testable, mirroring <see cref="SendJobArgs"/>.
/// </summary>
public static class SendMetricArgs
{
    public static ParsedMetricEvent Parse(IReadOnlyList<string> args)
    {
        string? name = null, unit = null, kind = null, correlationId = null, instance = null;
        double? value = null;
        var sawValue = false;
        var attributes = new Dictionary<string, object?>();
        var errors = new List<string>();

        string? Next(ref int i) => i + 1 < args.Count ? args[++i] : null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--name": name = Next(ref i); break;
                case "--unit": unit = Next(ref i); break;
                case "--kind": kind = Next(ref i); break;
                case "--correlation-id": correlationId = Next(ref i); break;
                case "--instance": instance = Next(ref i); break;
                case "--value":
                {
                    var raw = Next(ref i);
                    if (raw is null) break;
                    sawValue = true;
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        value = d;
                    }
                    else
                    {
                        errors.Add($"Invalid --value '{raw}'.");
                    }
                    break;
                }
                case "--attr":
                {
                    var raw = Next(ref i);
                    if (raw is null)
                    {
                        errors.Add("--attr requires a key=value argument.");
                        break;
                    }
                    if (SendJobArgs.TryParseAttribute(raw, out var key, out var attrValue))
                    {
                        attributes[key] = attrValue;
                    }
                    else
                    {
                        errors.Add($"Invalid --attr '{raw}'. Expected key=value.");
                    }
                    break;
                }
                default:
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("--name is required.");
        }

        if (!sawValue)
        {
            errors.Add("--value is required.");
        }

        if (errors.Count > 0)
        {
            return new ParsedMetricEvent(null, errors);
        }

        var evt = new MetricEvent
        {
            Name = name!,
            Value = value!.Value,
            Unit = unit,
            Kind = kind ?? "gauge",
            CorrelationId = correlationId,
            Instance = instance,
            Attributes = attributes.Count > 0 ? attributes : null
        };

        return new ParsedMetricEvent(evt, errors);
    }
}
