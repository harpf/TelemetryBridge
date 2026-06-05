using System.Text.Json;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

public sealed class ReportService
{
    public AdvancedReport CreateAdvancedReport(string bufferPath, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null)
    {
        if (!Directory.Exists(bufferPath))
        {
            return new AdvancedReport();
        }

        var events = new List<JobEvent>();

        foreach (var file in Directory.EnumerateFiles(bufferPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var payload = File.ReadAllText(file);
                // Use the shared camelCase, case-insensitive options so reports read
                // the buffer files written by BufferService (same options). Default
                // options would silently fail to bind the camelCase payloads.
                var jobEvent = JsonSerializer.Deserialize<JobEvent>(payload, ConfigService.JsonOptions);
                if (jobEvent is null) continue;

                if (fromUtc.HasValue && jobEvent.StartedAt < fromUtc.Value) continue;
                if (toUtc.HasValue && jobEvent.StartedAt > toUtc.Value) continue;

                events.Add(jobEvent);
            }
            catch
            {
                // Skip invalid files
            }
        }

        return BuildReport(events);
    }

    private static AdvancedReport BuildReport(List<JobEvent> events)
    {
        var durationSamples = events.Where(e => e.DurationMs.HasValue).Select(e => e.DurationMs!.Value).OrderBy(x => x).ToArray();
        var hasDurations = durationSamples.Length > 0;

        // All AdvancedReport properties are init-only, so build the whole object in
        // a single initializer rather than mutating after construction.
        return new AdvancedReport
        {
            TotalEvents = events.Count,
            SucceededEvents = events.Count(e => string.Equals(e.Status, "succeeded", StringComparison.OrdinalIgnoreCase)),
            FailedEvents = events.Count(e => string.Equals(e.Status, "failed", StringComparison.OrdinalIgnoreCase)),
            AvgDurationMs = hasDurations ? durationSamples.Average() : null,
            P50DurationMs = hasDurations ? Percentile(durationSamples, 0.5) : null,
            P95DurationMs = hasDurations ? Percentile(durationSamples, 0.95) : null,
            MaxDurationMs = hasDurations ? durationSamples[^1] : null,
            ByStatus = events
                .GroupBy(e => e.Status, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            BySystem = events
                .GroupBy(e => e.System, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            TopFailedJobs = events
                .Where(e => string.Equals(e.Status, "failed", StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => e.JobName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new JobFailureSummary(g.Key, g.Count()))
                .OrderByDescending(x => x.Failures)
                .ThenBy(x => x.JobName)
                .Take(10)
                .ToList()
        };
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;
        if (sortedValues.Length == 1) return sortedValues[0];

        var index = (sortedValues.Length - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);

        if (lower == upper) return sortedValues[lower];

        var weight = index - lower;
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * weight;
    }
}

public sealed class AdvancedReport
{
    public int TotalEvents { get; init; }
    public int SucceededEvents { get; init; }
    public int FailedEvents { get; init; }
    public double? AvgDurationMs { get; init; }
    public double? P50DurationMs { get; init; }
    public double? P95DurationMs { get; init; }
    public double? MaxDurationMs { get; init; }
    public Dictionary<string, int> ByStatus { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> BySystem { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<JobFailureSummary> TopFailedJobs { get; init; } = [];
}

public sealed record JobFailureSummary(string JobName, int Failures);
