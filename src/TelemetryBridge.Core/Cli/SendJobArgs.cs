using System.Globalization;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Cli;

/// <summary>Outcome of parsing the <c>send job</c> argument list.</summary>
public sealed record ParsedJobEvent(JobEvent? Event, IReadOnlyList<string> Errors);

/// <summary>
/// Parses the flags for the <c>send job</c> command into a <see cref="JobEvent"/>.
/// Kept in Core (not Program.cs) so the argument layer is unit-testable.
/// </summary>
public static class SendJobArgs
{
    public static ParsedJobEvent Parse(IReadOnlyList<string> args)
    {
        string? jobName = null, status = null, system = null, eventType = null;
        string? correlationId = null, parentCorrelationId = null, instance = null;
        string? scriptPath = null, scriptName = null, scriptVersion = null;
        string? errorMessage = null, errorType = null, errorCode = null;
        int? exitCode = null;
        double? durationMs = null;
        var attributes = new Dictionary<string, object?>();
        var errors = new List<string>();

        string? Next(ref int i) => i + 1 < args.Count ? args[++i] : null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--job-name": jobName = Next(ref i); break;
                case "--status": status = Next(ref i); break;
                case "--system": system = Next(ref i); break;
                case "--event-type": eventType = Next(ref i); break;
                case "--correlation-id": correlationId = Next(ref i); break;
                case "--parent-correlation-id": parentCorrelationId = Next(ref i); break;
                case "--instance": instance = Next(ref i); break;
                case "--script-path": scriptPath = Next(ref i); break;
                case "--script-name": scriptName = Next(ref i); break;
                case "--script-version": scriptVersion = Next(ref i); break;
                case "--error-message": errorMessage = Next(ref i); break;
                case "--error-type": errorType = Next(ref i); break;
                case "--error-code": errorCode = Next(ref i); break;
                case "--duration-ms":
                {
                    var raw = Next(ref i);
                    if (raw is null) break;
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        durationMs = d;
                    }
                    else
                    {
                        errors.Add($"Invalid --duration-ms '{raw}'.");
                    }
                    break;
                }
                case "--exit-code":
                {
                    var raw = Next(ref i);
                    if (raw is null) break;
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    {
                        exitCode = n;
                    }
                    else
                    {
                        errors.Add($"Invalid --exit-code '{raw}'.");
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
                    if (TryParseAttribute(raw, out var key, out var value))
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
                    // Unknown tokens are ignored; global flags are stripped upstream.
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(jobName))
        {
            errors.Add("--job-name is required.");
        }

        if (errors.Count > 0)
        {
            return new ParsedJobEvent(null, errors);
        }

        JobError? error = null;
        if (!string.IsNullOrWhiteSpace(errorMessage) ||
            !string.IsNullOrWhiteSpace(errorType) ||
            !string.IsNullOrWhiteSpace(errorCode))
        {
            error = new JobError { Message = errorMessage, Type = errorType, Code = errorCode };
        }

        var evt = new JobEvent
        {
            JobName = jobName!,
            Status = status ?? "succeeded",
            System = system ?? "powershell",
            EventType = eventType ?? "job",
            CorrelationId = correlationId,
            ParentCorrelationId = parentCorrelationId,
            Instance = instance,
            ScriptPath = scriptPath,
            ScriptName = scriptName,
            ScriptVersion = scriptVersion,
            DurationMs = durationMs,
            ExitCode = exitCode,
            Error = error,
            Attributes = attributes.Count > 0 ? attributes : null
        };

        return new ParsedJobEvent(evt, errors);
    }

    /// <summary>
    /// Parses a <c>key=value</c> token. The value type is inferred: <c>null</c>,
    /// <c>true</c>/<c>false</c>, integer, decimal, otherwise a string.
    /// </summary>
    public static bool TryParseAttribute(string token, out string key, out object? value)
    {
        key = string.Empty;
        value = null;
        if (string.IsNullOrEmpty(token)) return false;

        var idx = token.IndexOf('=');
        if (idx <= 0) return false; // require a non-empty key and a separator

        key = token[..idx];
        value = InferValue(token[(idx + 1)..]);
        return true;
    }

    private static object? InferValue(string raw)
    {
        if (string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return raw;
    }
}
