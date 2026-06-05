using System.Globalization;
using System.Text.Json;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

/// <summary>
/// Durable, crash-safe queue of telemetry events on disk.
///
/// Retry metadata (enqueue time, attempt count, next-eligible time) is encoded
/// in the file name so it survives process restarts without a sidecar:
///
///   {enqueuedUnixMs}-{attempts}-{nextEligibleUnixMs}__{jobRunId}.json
///
/// State transitions (pending -&gt; retrying -&gt; dead-letter and back) are
/// performed with atomic <see cref="File.Move(string, string)"/> renames so a
/// crash can never leave an item in an ambiguous state. Enqueue writes to a
/// temporary file and then renames it into place, so a crash mid-write cannot
/// leave a half-written JSON file in the queue.
/// </summary>
public sealed class BufferService
{
    private const string FileExtension = ".json";
    private const string TempExtension = ".tmp";
    private const string MetaSeparator = "__";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly Func<DateTimeOffset> _clock;

    public BufferService(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Writes an event to the buffer crash-safely: the payload is written to a
    /// temporary file and then atomically renamed into place. Returns the path
    /// to the durable file.
    /// </summary>
    public string Enqueue(JobEvent telemetryEvent, string bufferPath)
    {
        Directory.CreateDirectory(bufferPath);

        var enqueuedAt = _clock();
        var fileName = BuildFileName(enqueuedAt.ToUnixTimeMilliseconds(), 0, 0, telemetryEvent.JobRunId);
        var fullPath = Path.Combine(bufferPath, fileName);
        var tempPath = fullPath + TempExtension;

        var payload = JsonSerializer.Serialize(telemetryEvent, SerializerOptions);
        File.WriteAllText(tempPath, payload);
        // Atomic publish: a reader either sees nothing or the complete file.
        File.Move(tempPath, fullPath, overwrite: true);

        return fullPath;
    }

    /// <summary>
    /// Deletes a buffered event file after it has been confirmed delivered.
    /// Safe to call when the file is already gone.
    /// </summary>
    public void Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Enumerates the durable items in a folder, ordered oldest-first. Stray
    /// temp/partial files and anything that does not match the queue naming
    /// convention are ignored, so a crash mid-write is invisible to readers.
    /// </summary>
    public IReadOnlyList<BufferItem> List(string folder, bool deadLetter = false)
    {
        if (!Directory.Exists(folder)) return Array.Empty<BufferItem>();

        var items = new List<BufferItem>();
        foreach (var path in Directory.EnumerateFiles(folder, "*" + FileExtension))
        {
            // EnumerateFiles("*.json") still matches "foo.json.tmp" on some
            // platforms; require an exact .json extension and a parseable name.
            if (!path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)) continue;

            var fileName = Path.GetFileName(path);
            if (!TryParseFileName(fileName, out var enqueuedMs, out var attempts, out var nextMs, out _))
            {
                continue;
            }

            var evt = Read(path);
            if (evt is null) continue;

            var state = deadLetter
                ? BufferItemState.DeadLetter
                : attempts > 0 ? BufferItemState.Retrying : BufferItemState.Pending;

            items.Add(new BufferItem
            {
                Path = path,
                Event = evt,
                Attempts = attempts,
                EnqueuedAt = DateTimeOffset.FromUnixTimeMilliseconds(enqueuedMs),
                NextEligibleAt = nextMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(nextMs) : null,
                State = state,
                SizeBytes = new FileInfo(path).Length
            });
        }

        return items
            .OrderBy(i => i.EnqueuedAt)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Deserializes the event stored at <paramref name="path"/>, or null if unreadable.</summary>
    public JobEvent? Read(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<JobEvent>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records a failed attempt: increments the attempt counter and stores the
    /// next-eligible time durably by renaming the file. Returns the new path.
    /// </summary>
    public string MarkRetrying(BufferItem item, int attempts, DateTimeOffset nextEligibleAt)
    {
        var folder = Path.GetDirectoryName(item.Path)!;
        var fileName = BuildFileName(
            item.EnqueuedAt.ToUnixTimeMilliseconds(),
            attempts,
            nextEligibleAt.ToUnixTimeMilliseconds(),
            item.Event.JobRunId);
        return Rename(item.Path, Path.Combine(folder, fileName));
    }

    /// <summary>
    /// Moves an item into the dead-letter folder, preserving its metadata.
    /// Returns the new path.
    /// </summary>
    public string MoveToDeadLetter(BufferItem item, string deadLetterPath)
    {
        Directory.CreateDirectory(deadLetterPath);
        var target = Path.Combine(deadLetterPath, Path.GetFileName(item.Path));
        return Rename(item.Path, target);
    }

    /// <summary>
    /// Resets an item back to pending in <paramref name="bufferPath"/>: clears
    /// the attempt counter and next-eligible time. Works for both retrying and
    /// dead-lettered items. Returns the new path.
    /// </summary>
    public string ResetToPending(BufferItem item, string bufferPath)
    {
        Directory.CreateDirectory(bufferPath);
        var fileName = BuildFileName(
            item.EnqueuedAt.ToUnixTimeMilliseconds(),
            0,
            0,
            item.Event.JobRunId);
        return Rename(item.Path, Path.Combine(bufferPath, fileName));
    }

    private static string Rename(string source, string target)
    {
        if (!string.Equals(source, target, StringComparison.Ordinal))
        {
            File.Move(source, target, overwrite: true);
        }
        return target;
    }

    internal static string BuildFileName(long enqueuedMs, int attempts, long nextMs, string jobRunId)
    {
        var safeId = Sanitize(jobRunId);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{enqueuedMs}-{attempts}-{nextMs}{MetaSeparator}{safeId}{FileExtension}");
    }

    internal static bool TryParseFileName(
        string fileName,
        out long enqueuedMs,
        out int attempts,
        out long nextMs,
        out string jobRunId)
    {
        enqueuedMs = 0;
        attempts = 0;
        nextMs = 0;
        jobRunId = string.Empty;

        if (!fileName.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)) return false;

        var stem = fileName[..^FileExtension.Length];
        var sep = stem.IndexOf(MetaSeparator, StringComparison.Ordinal);
        if (sep < 0) return false;

        var meta = stem[..sep];
        jobRunId = stem[(sep + MetaSeparator.Length)..];

        var parts = meta.Split('-');
        if (parts.Length != 3) return false;

        return long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out enqueuedMs)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out attempts)
            && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out nextMs);
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "event";
        // Keep file names well-behaved and free of our separators.
        Span<char> buffer = stackalloc char[value.Length];
        var invalid = Path.GetInvalidFileNameChars();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            buffer[i] = c == '-' || c == '_' || Array.IndexOf(invalid, c) >= 0 ? '.' : c;
        }
        return new string(buffer);
    }
}
