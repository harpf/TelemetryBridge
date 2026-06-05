using System.Text.Json;
using TelemetryBridge.Core.Models;

namespace TelemetryBridge.Core.Services;

public sealed class BufferService
{
    public string Enqueue(JobEvent telemetryEvent, string bufferPath)
    {
        Directory.CreateDirectory(bufferPath);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{telemetryEvent.JobRunId}.json";
        var fullPath = Path.Combine(bufferPath, fileName);
        var payload = JsonSerializer.Serialize(telemetryEvent, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(fullPath, payload);
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
}
