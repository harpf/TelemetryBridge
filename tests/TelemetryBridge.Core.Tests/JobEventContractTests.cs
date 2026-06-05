using System.Text.Json;
using TelemetryBridge.Core.Cli;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

/// <summary>
/// Verifies that <see cref="JobEvent"/>, its serialization, validation and the
/// <see cref="ExportService"/> span mapping all line up with
/// docs/Event-Contract.json (the ground-truth schema).
/// </summary>
public sealed class JobEventContractTests
{
    private static JobEvent FullEvent() => new()
    {
        EventType = "job-end",
        JobRunId = "11111111-1111-1111-1111-111111111111",
        CorrelationId = "corr-1",
        ParentCorrelationId = "corr-0",
        System = "scriptrunner",
        Instance = "host-7",
        JobName = "nightly-backup",
        ScriptPath = @"C:\scripts\backup.ps1",
        ScriptName = "backup.ps1",
        ScriptVersion = "1.4.2",
        Status = "failed",
        StartedAt = DateTimeOffset.Parse("2026-06-05T10:00:00Z"),
        FinishedAt = DateTimeOffset.Parse("2026-06-05T10:05:00Z"),
        DurationMs = 300_000,
        ExitCode = 2,
        Error = new JobError
        {
            Type = "System.IO.IOException",
            Message = "disk full",
            Code = "ERR_DISK",
            Category = "WriteError",
            FullyQualifiedId = "Backup,Microsoft.PowerShell.Commands",
            ScriptLineNumber = 42,
            ScriptColumnNumber = 7,
            CommandName = "Copy-Item",
            StackTrace = "at Backup() line 42"
        },
        Attributes = new Dictionary<string, object?>
        {
            ["region"] = "eu-central",
            ["retryCount"] = 3L,
            ["isDryRun"] = true,
            ["note"] = null
        }
    };

    // ---- Round-trip + camelCase serialization ----------------------------

    [Fact]
    public void JobEvent_RoundTrips_AllNewFields()
    {
        var original = FullEvent();

        var json = JsonSerializer.Serialize(original, ConfigService.JsonOptions);
        var copy = JsonSerializer.Deserialize<JobEvent>(json, ConfigService.JsonOptions)!;

        Assert.Equal(original.EventType, copy.EventType);
        Assert.Equal(original.JobRunId, copy.JobRunId);
        Assert.Equal(original.CorrelationId, copy.CorrelationId);
        Assert.Equal(original.ParentCorrelationId, copy.ParentCorrelationId);
        Assert.Equal(original.System, copy.System);
        Assert.Equal(original.Instance, copy.Instance);
        Assert.Equal(original.JobName, copy.JobName);
        Assert.Equal(original.ScriptPath, copy.ScriptPath);
        Assert.Equal(original.ScriptName, copy.ScriptName);
        Assert.Equal(original.ScriptVersion, copy.ScriptVersion);
        Assert.Equal(original.Status, copy.Status);
        Assert.Equal(original.StartedAt, copy.StartedAt);
        Assert.Equal(original.FinishedAt, copy.FinishedAt);
        Assert.Equal(original.DurationMs, copy.DurationMs);
        Assert.Equal(original.ExitCode, copy.ExitCode);

        Assert.NotNull(copy.Error);
        Assert.Equal("System.IO.IOException", copy.Error!.Type);
        Assert.Equal("disk full", copy.Error.Message);
        Assert.Equal("ERR_DISK", copy.Error.Code);
        Assert.Equal("WriteError", copy.Error.Category);
        Assert.Equal("Backup,Microsoft.PowerShell.Commands", copy.Error.FullyQualifiedId);
        Assert.Equal(42, copy.Error.ScriptLineNumber);
        Assert.Equal(7, copy.Error.ScriptColumnNumber);
        Assert.Equal("Copy-Item", copy.Error.CommandName);
        Assert.Equal("at Backup() line 42", copy.Error.StackTrace);
    }

    [Fact]
    public void JobEvent_Serializes_AsCamelCase_MatchingContract()
    {
        var json = JsonSerializer.Serialize(FullEvent(), ConfigService.JsonOptions);

        Assert.Contains("\"eventType\"", json);
        Assert.Contains("\"jobRunId\"", json);
        Assert.Contains("\"correlationId\"", json);
        Assert.Contains("\"parentCorrelationId\"", json);
        Assert.Contains("\"scriptPath\"", json);
        Assert.Contains("\"exitCode\"", json);
        Assert.Contains("\"error\"", json);
        Assert.Contains("\"fullyQualifiedId\"", json);
        Assert.Contains("\"attributes\"", json);
        // No PascalCase leakage and no legacy flat field on the way out.
        Assert.DoesNotContain("\"JobName\"", json);
        Assert.DoesNotContain("\"errorMessage\"", json);
    }

    // ---- Backward compatibility with old flat buffered files -------------

    [Fact]
    public void OldFormatBufferedFile_WithFlatErrorMessage_StillLoads()
    {
        // The shape BufferService used to write before P2: PascalCase property
        // names and a flat "errorMessage" string instead of a structured error.
        var legacyJson = """
        {
          "EventType": "job",
          "JobRunId": "22222222-2222-2222-2222-222222222222",
          "System": "powershell",
          "JobName": "legacy-job",
          "Status": "failed",
          "DurationMs": 1200,
          "StartedAt": "2026-01-01T00:00:00+00:00",
          "FinishedAt": "2026-01-01T00:00:01+00:00",
          "ErrorMessage": "boom"
        }
        """;

        var dir = Path.Combine(Path.GetTempPath(), "tb-legacy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "1700000000000-0-0__legacy.json");
            File.WriteAllText(path, legacyJson);

            var evt = new BufferService().Read(path);

            Assert.NotNull(evt);
            Assert.Equal("legacy-job", evt!.JobName);
            Assert.Equal("failed", evt.Status);
            Assert.Equal(1200, evt.DurationMs);
            // The flat errorMessage is folded into the structured error object.
            Assert.NotNull(evt.Error);
            Assert.Equal("boom", evt.Error!.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BufferService_WritesCamelCaseFiles_MatchingContract()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tb-camel", Guid.NewGuid().ToString("N"));
        try
        {
            var path = new BufferService().Enqueue(FullEvent(), dir);
            var text = File.ReadAllText(path);

            Assert.Contains("\"jobName\"", text);
            Assert.Contains("\"correlationId\"", text);
            Assert.DoesNotContain("\"JobName\"", text);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Contract enums --------------------------------------------------

    [Theory]
    [InlineData("job")]
    [InlineData("job-start")]
    [InlineData("job-end")]
    public void EventContract_AcceptsValidEventTypes(string value) =>
        Assert.True(EventContract.IsValidEventType(value));

    [Theory]
    [InlineData("started")]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("warning")]
    [InlineData("skipped")]
    [InlineData("cancelled")]
    [InlineData("timeout")]
    [InlineData("interrupted")]
    [InlineData("unknown")]
    public void EventContract_AcceptsValidStatuses(string value) =>
        Assert.True(EventContract.IsValidStatus(value));

    [Theory]
    [InlineData("powershell")]
    [InlineData("scriptrunner")]
    [InlineData("simego-dss")]
    [InlineData("ouvvi")]
    [InlineData("manual")]
    public void EventContract_AcceptsValidSystems(string value) =>
        Assert.True(EventContract.IsValidSystem(value));

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData(null)]
    public void EventContract_RejectsUnknownStatuses(string? value) =>
        Assert.False(EventContract.IsValidStatus(value));

    // ---- Validation / normalization (send path) --------------------------

    [Fact]
    public void Validate_ValidEvent_ProducesNoWarningsOrErrors()
    {
        var outcome = JobEventValidator.ValidateAndNormalize(FullEvent(), strict: false);

        Assert.Empty(outcome.Warnings);
        Assert.Empty(outcome.Errors);
        Assert.False(outcome.HasErrors);
    }

    [Fact]
    public void Validate_InvalidStatus_NonStrict_NormalizesToUnknown_WithWarning()
    {
        var evt = new JobEvent { JobName = "j", Status = "bogus", System = "powershell" };

        var outcome = JobEventValidator.ValidateAndNormalize(evt, strict: false);

        Assert.False(outcome.HasErrors);
        Assert.NotEmpty(outcome.Warnings);
        Assert.Equal("unknown", outcome.Event.Status);
    }

    [Fact]
    public void Validate_InvalidStatus_Strict_ReportsError()
    {
        var evt = new JobEvent { JobName = "j", Status = "bogus", System = "powershell" };

        var outcome = JobEventValidator.ValidateAndNormalize(evt, strict: true);

        Assert.True(outcome.HasErrors);
    }

    [Fact]
    public void Validate_InvalidSystem_NonStrict_NormalizesToManual_WithWarning()
    {
        var evt = new JobEvent { JobName = "j", Status = "succeeded", System = "nope" };

        var outcome = JobEventValidator.ValidateAndNormalize(evt, strict: false);

        Assert.False(outcome.HasErrors);
        Assert.NotEmpty(outcome.Warnings);
        Assert.Equal("manual", outcome.Event.System);
    }

    [Fact]
    public void Validate_InvalidEventType_NonStrict_NormalizesToJob_WithWarning()
    {
        var evt = new JobEvent { JobName = "j", Status = "succeeded", System = "powershell", EventType = "weird" };

        var outcome = JobEventValidator.ValidateAndNormalize(evt, strict: false);

        Assert.False(outcome.HasErrors);
        Assert.NotEmpty(outcome.Warnings);
        Assert.Equal("job", outcome.Event.EventType);
    }

    // ---- Span mapping ----------------------------------------------------

    [Fact]
    public void SpanMapping_ProjectsCoreAndNewFields_AsTags()
    {
        var mapping = ExportService.BuildSpanMapping(FullEvent());

        Assert.Equal("job-end", mapping.Tags["event.type"]);
        Assert.Equal("nightly-backup", mapping.Tags["job.name"]);
        Assert.Equal("failed", mapping.Tags["job.status"]);
        Assert.Equal("scriptrunner", mapping.Tags["job.system"]);
        Assert.Equal("corr-1", mapping.Tags["job.correlation_id"]);
        Assert.Equal("corr-0", mapping.Tags["job.parent_correlation_id"]);
        Assert.Equal("host-7", mapping.Tags["job.instance"]);
        Assert.Equal(@"C:\scripts\backup.ps1", mapping.Tags["job.script_path"]);
        Assert.Equal("backup.ps1", mapping.Tags["job.script_name"]);
        Assert.Equal("1.4.2", mapping.Tags["job.script_version"]);
        Assert.Equal(2, mapping.Tags["job.exit_code"]);
    }

    [Fact]
    public void SpanMapping_ProjectsStructuredError_OntoErrorTagsAndExceptionEvent()
    {
        var mapping = ExportService.BuildSpanMapping(FullEvent());

        Assert.True(mapping.IsError);
        Assert.Equal("System.IO.IOException", mapping.Tags["error.type"]);
        Assert.Equal("disk full", mapping.Tags["error.message"]);
        Assert.Equal("ERR_DISK", mapping.Tags["error.code"]);
        Assert.Equal("WriteError", mapping.Tags["error.category"]);
        Assert.Equal(42, mapping.Tags["error.script_line_number"]);
        Assert.Equal("Copy-Item", mapping.Tags["error.command_name"]);

        Assert.NotNull(mapping.ExceptionEvent);
        Assert.Equal("System.IO.IOException", mapping.ExceptionEvent!["exception.type"]);
        Assert.Equal("disk full", mapping.ExceptionEvent["exception.message"]);
        Assert.Equal("at Backup() line 42", mapping.ExceptionEvent["exception.stacktrace"]);
    }

    [Fact]
    public void SpanMapping_ProjectsAttributes_AsPrefixedTags_AfterJsonRoundTrip()
    {
        // Round-trip first so attribute values arrive as JsonElement, exactly as
        // they would when an event is drained from the durable buffer.
        var json = JsonSerializer.Serialize(FullEvent(), ConfigService.JsonOptions);
        var fromDisk = JsonSerializer.Deserialize<JobEvent>(json, ConfigService.JsonOptions)!;

        var mapping = ExportService.BuildSpanMapping(fromDisk);

        Assert.Equal("eu-central", mapping.Tags["attr.region"]);
        Assert.Equal(3L, Convert.ToInt64(mapping.Tags["attr.retryCount"]));
        Assert.Equal(true, mapping.Tags["attr.isDryRun"]);
        // Null-valued attributes are skipped rather than emitted as empty tags.
        Assert.False(mapping.Tags.ContainsKey("attr.note"));
    }

    [Fact]
    public void SpanMapping_OkStatus_HasNoErrorTagsOrExceptionEvent()
    {
        var evt = new JobEvent { JobName = "j", Status = "succeeded", System = "powershell" };

        var mapping = ExportService.BuildSpanMapping(evt);

        Assert.False(mapping.IsError);
        Assert.Null(mapping.ExceptionEvent);
        Assert.False(mapping.Tags.ContainsKey("error.message"));
    }

    // ---- CLI argument layer ---------------------------------------------

    [Fact]
    public void SendJobArgs_Parse_BuildsEventFromAllFlags()
    {
        string[] args =
        {
            "--job-name", "build",
            "--status", "failed",
            "--system", "scriptrunner",
            "--event-type", "job-end",
            "--correlation-id", "c1",
            "--parent-correlation-id", "c0",
            "--instance", "host-1",
            "--script-path", @"C:\s.ps1",
            "--script-name", "s.ps1",
            "--script-version", "2.0",
            "--exit-code", "3",
            "--error-message", "kaboom",
            "--error-type", "InvalidOperation",
            "--error-code", "ERR_X"
        };

        var parsed = SendJobArgs.Parse(args);

        Assert.Empty(parsed.Errors);
        var evt = parsed.Event!;
        Assert.Equal("build", evt.JobName);
        Assert.Equal("failed", evt.Status);
        Assert.Equal("scriptrunner", evt.System);
        Assert.Equal("job-end", evt.EventType);
        Assert.Equal("c1", evt.CorrelationId);
        Assert.Equal("c0", evt.ParentCorrelationId);
        Assert.Equal("host-1", evt.Instance);
        Assert.Equal(@"C:\s.ps1", evt.ScriptPath);
        Assert.Equal("s.ps1", evt.ScriptName);
        Assert.Equal("2.0", evt.ScriptVersion);
        Assert.Equal(3, evt.ExitCode);
        Assert.NotNull(evt.Error);
        Assert.Equal("kaboom", evt.Error!.Message);
        Assert.Equal("InvalidOperation", evt.Error.Type);
        Assert.Equal("ERR_X", evt.Error.Code);
    }

    [Fact]
    public void SendJobArgs_Parse_RepeatableAttr_BuildsTypedAttributes()
    {
        string[] args =
        {
            "--job-name", "j",
            "--attr", "region=eu",
            "--attr", "count=5",
            "--attr", "flag=true",
            "--attr", "empty=null"
        };

        var parsed = SendJobArgs.Parse(args);

        Assert.Empty(parsed.Errors);
        var attrs = parsed.Event!.Attributes!;
        Assert.Equal("eu", attrs["region"]);
        Assert.Equal(5L, attrs["count"]);
        Assert.Equal(true, attrs["flag"]);
        Assert.Null(attrs["empty"]);
    }

    [Fact]
    public void SendJobArgs_Parse_MissingJobName_ReportsError()
    {
        var parsed = SendJobArgs.Parse(new[] { "--status", "succeeded" });

        Assert.NotEmpty(parsed.Errors);
        Assert.Null(parsed.Event);
    }

    [Theory]
    [InlineData("k=hello", "hello")]
    [InlineData("k=42", 42L)]
    [InlineData("k=3.5", 3.5)]
    [InlineData("k=true", true)]
    [InlineData("k=false", false)]
    [InlineData("k=null", null)]
    public void SendJobArgs_TryParseAttribute_InfersValueType(string token, object? expected)
    {
        Assert.True(SendJobArgs.TryParseAttribute(token, out var key, out var value));
        Assert.Equal("k", key);
        Assert.Equal(expected, value);
    }
}
