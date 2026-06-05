using System.Text;
using System.Text.Json;
using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Ingest;
using static TelemetryBridge.Service.Tests.TestSupport;

namespace TelemetryBridge.Service.Tests;

public sealed class JobEventIngestHandlerTests
{
    private const string ValidJson =
        "{\"jobName\":\"nightly-sync\",\"status\":\"succeeded\",\"system\":\"powershell\"," +
        "\"startedAt\":\"2026-01-01T00:00:00Z\"}";

    [Fact]
    public void Valid_event_is_validated_enqueued_and_accepted_202()
    {
        var dir = NewTempDir();
        var buffer = new BufferService();
        var handler = new JobEventIngestHandler(buffer, Options(bufferPath: dir));

        var result = handler.Handle(ValidJson);

        Assert.Equal(202, result.StatusCode);
        var buffered = buffer.List(dir);
        Assert.Single(buffered);
        Assert.Equal("nightly-sync", buffered[0].Event.JobName);
    }

    [Fact]
    public void Invalid_json_is_rejected_400_and_nothing_is_enqueued()
    {
        var dir = NewTempDir();
        var buffer = new BufferService();
        var handler = new JobEventIngestHandler(buffer, Options(bufferPath: dir));

        var result = handler.Handle("{ this is not json ");

        Assert.Equal(400, result.StatusCode);
        Assert.Empty(buffer.List(dir));
    }

    [Fact]
    public void Empty_body_is_rejected_400()
    {
        var dir = NewTempDir();
        var handler = new JobEventIngestHandler(new BufferService(), Options(bufferPath: dir));

        Assert.Equal(400, handler.Handle("   ").StatusCode);
    }

    [Fact]
    public async Task HandleAsync_reads_the_request_stream_and_enqueues()
    {
        var dir = NewTempDir();
        var buffer = new BufferService();
        var handler = new JobEventIngestHandler(buffer, Options(bufferPath: dir));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ValidJson));
        var result = await handler.HandleAsync(stream);

        Assert.Equal(202, result.StatusCode);
        Assert.Single(buffer.List(dir));
    }

    [Fact]
    public void Strict_mode_rejects_an_invalid_status_400()
    {
        var dir = NewTempDir();
        var buffer = new BufferService();
        var handler = new JobEventIngestHandler(buffer, Options(bufferPath: dir, strict: true));

        var json = "{\"jobName\":\"j\",\"status\":\"definitely-not-a-status\",\"system\":\"powershell\"}";
        var result = handler.Handle(json);

        Assert.Equal(400, result.StatusCode);
        Assert.Empty(buffer.List(dir));
    }

    [Fact]
    public void Nonstrict_normalizes_an_invalid_status_and_still_enqueues()
    {
        var dir = NewTempDir();
        var buffer = new BufferService();
        var handler = new JobEventIngestHandler(buffer, Options(bufferPath: dir, strict: false));

        var json = "{\"jobName\":\"j\",\"status\":\"definitely-not-a-status\",\"system\":\"powershell\"}";
        var result = handler.Handle(json);

        Assert.Equal(202, result.StatusCode);
        var buffered = buffer.List(dir);
        Assert.Single(buffered);
        Assert.Equal("unknown", buffered[0].Event.Status); // normalized to a contract member

        // The response surfaces the normalization as a warning.
        using var doc = JsonDocument.Parse(result.Body);
        Assert.NotEmpty(doc.RootElement.GetProperty("warnings").EnumerateArray());
    }
}
