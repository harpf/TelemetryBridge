using System.Text.Json;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Configuration;

namespace TelemetryBridge.Service.Ingest;

/// <summary>Outcome of an ingest attempt: an HTTP status code and a JSON body.</summary>
public sealed record IngestResult(int StatusCode, string Body)
{
    public bool Accepted => StatusCode == 202;
}

/// <summary>
/// The testable core of the localhost HTTP ingest endpoint: parse a camelCase
/// <see cref="JobEvent"/> (same contract as the CLI), validate its closed-enum
/// fields, enqueue it on the durable buffer, and return 202. Invalid JSON → 400;
/// in strict mode an invalid enum value → 400, otherwise it is normalized and a
/// warning is surfaced in the response body.
///
/// Pure with respect to HTTP: it takes a JSON string / stream and returns a
/// status + body, so it can be unit tested without a socket.
/// </summary>
public sealed class JobEventIngestHandler
{
    private readonly BufferService _buffer;
    private readonly ServiceHostOptions _options;

    public JobEventIngestHandler(BufferService buffer, ServiceHostOptions options)
    {
        _buffer = buffer;
        _options = options;
    }

    public async Task<IngestResult> HandleAsync(Stream body, CancellationToken cancellationToken = default)
    {
        string json;
        using (var reader = new StreamReader(body))
        {
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        return Handle(json);
    }

    public IngestResult Handle(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Error(400, "Request body is empty; expected a JobEvent JSON document.");
        }

        JobEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<JobEvent>(json, ConfigService.JsonOptions);
        }
        catch (JsonException ex)
        {
            return Error(400, $"Invalid JobEvent JSON: {ex.Message}");
        }

        if (evt is null)
        {
            return Error(400, "Request body did not deserialize to a JobEvent.");
        }

        // Validate the closed-enum fields against the contract: in non-strict
        // mode normalize + warn; in strict mode reject so bad data never lands
        // in the durable buffer.
        var outcome = JobEventValidator.ValidateAndNormalize(evt, _options.Bridge.Agent.StrictMode);
        if (outcome.HasErrors)
        {
            return Error(400, string.Join(" ", outcome.Errors));
        }

        var path = _buffer.Enqueue(outcome.Event, _options.Bridge.Buffer.Path);

        var body = JsonSerializer.Serialize(new
        {
            status = "accepted",
            jobRunId = outcome.Event.JobRunId,
            buffered = Path.GetFileName(path),
            warnings = outcome.Warnings
        }, ConfigService.JsonOptions);

        return new IngestResult(202, body);
    }

    private static IngestResult Error(int statusCode, string message) =>
        new(statusCode, JsonSerializer.Serialize(new { status = "rejected", error = message }, ConfigService.JsonOptions));
}
