using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Cli;

/// <summary>Outcome of parsing the <c>send log</c> argument list.</summary>
public sealed record ParsedLogEvent(LogEvent? Event, IReadOnlyList<string> Errors);

/// <summary>
/// Parses the flags for the <c>send log</c> command into a <see cref="LogEvent"/>.
/// Kept in Core (not Program.cs) so the argument layer is unit-testable, mirroring
/// <see cref="SendJobArgs"/>.
/// </summary>
public static class SendLogArgs
{
    public static ParsedLogEvent Parse(IReadOnlyList<string> args)
    {
        string? message = null, severity = null, correlationId = null, instance = null;
        var attributes = new Dictionary<string, object?>();
        var errors = new List<string>();

        string? Next(ref int i) => i + 1 < args.Count ? args[++i] : null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--message": message = Next(ref i); break;
                case "--severity": severity = Next(ref i); break;
                case "--correlation-id": correlationId = Next(ref i); break;
                case "--instance": instance = Next(ref i); break;
                case "--attr":
                {
                    var raw = Next(ref i);
                    if (raw is null)
                    {
                        errors.Add("--attr requires a key=value argument.");
                        break;
                    }
                    if (SendJobArgs.TryParseAttribute(raw, out var key, out var value))
                    {
                        attributes[key] = value;
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

        if (string.IsNullOrWhiteSpace(message))
        {
            errors.Add("--message is required.");
        }

        if (errors.Count > 0)
        {
            return new ParsedLogEvent(null, errors);
        }

        var evt = new LogEvent
        {
            Body = message!,
            Severity = severity ?? "info",
            CorrelationId = correlationId,
            Instance = instance,
            Attributes = attributes.Count > 0 ? attributes : null
        };

        return new ParsedLogEvent(evt, errors);
    }
}
