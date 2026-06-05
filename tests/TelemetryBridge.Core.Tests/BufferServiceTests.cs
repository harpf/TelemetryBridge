using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

public sealed class BufferServiceTests : IDisposable
{
    private readonly string _bufferPath =
        Path.Combine(Path.GetTempPath(), "tb-buffer-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Enqueue_WritesEventFileToBufferPath()
    {
        var service = new BufferService();
        var evt = new JobEvent { JobName = "nightly-backup", Status = "succeeded" };

        var path = service.Enqueue(evt, _bufferPath);

        Assert.True(File.Exists(path));
        Assert.StartsWith(_bufferPath, path);
        Assert.Contains("nightly-backup", File.ReadAllText(path));
    }

    [Fact]
    public void Delete_RemovesAPreviouslyEnqueuedFile()
    {
        var service = new BufferService();
        var path = service.Enqueue(new JobEvent { JobName = "job" }, _bufferPath);
        Assert.True(File.Exists(path));

        service.Delete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_OnMissingFile_DoesNotThrow()
    {
        var service = new BufferService();
        var missing = Path.Combine(_bufferPath, "does-not-exist.json");

        var exception = Record.Exception(() => service.Delete(missing));

        Assert.Null(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(_bufferPath))
        {
            Directory.Delete(_bufferPath, recursive: true);
        }
    }
}
