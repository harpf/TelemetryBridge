using System.Text.Json;
using TelemetryBridge.Core.Cli;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

var argsList = args;
var configService = new ConfigService();
var bufferService = new BufferService();
var exportService = new ExportService();
var diagnosticsService = new DiagnosticsService(exportService);
var reportService = new ReportService();
var serviceProbeService = new ServiceProbeService();

if (argsList.Length == 0 || argsList[0] is "-h" or "--help" or "help")
{
    PrintHelp(configService.GetWorkspaceRoot());
    return 0;
}

var verbose = argsList.Any(a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase));
argsList = argsList.Where(a => !string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase) && !string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase)).ToArray();

var strict = argsList.Any(a => string.Equals(a, "--strict", StringComparison.OrdinalIgnoreCase));
argsList = argsList.Where(a => !string.Equals(a, "--strict", StringComparison.OrdinalIgnoreCase)).ToArray();

var cmd = argsList[0].ToLowerInvariant();

return cmd switch
{
    "config" => HandleConfig(argsList.Skip(1).ToArray(), configService),
    "send" => await HandleSend(argsList.Skip(1).ToArray(), configService, bufferService, exportService, verbose, strict),
    "buffer" => await HandleBuffer(argsList.Skip(1).ToArray(), configService, bufferService, exportService, verbose),
    "diagnostics" or "diag" => await HandleDiagnostics(argsList.Skip(1).ToArray(), configService, diagnosticsService, verbose),
    "test" => await HandleTest(argsList.Skip(1).ToArray(), configService, diagnosticsService, verbose),
    "report" => HandleReport(argsList.Skip(1).ToArray(), configService, reportService),
    "service" => await HandleService(argsList.Skip(1).ToArray(), configService, serviceProbeService),
    _ => HandleUnknownCommand(cmd, configService)
};

static int HandleUnknownCommand(string cmd, ConfigService configService)
{
    Console.Error.WriteLine($"Unknown command '{cmd}'.\n");
    PrintHelp(configService.GetWorkspaceRoot());
    return 1;
}

static int HandleConfig(string[] args, ConfigService configService)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Missing config subcommand. Use: config init|show|validate");
        return 1;
    }

    var sub = args[0].ToLowerInvariant();
    switch (sub)
    {
        case "init":
            var cfg = configService.Init();
            Console.WriteLine($"Configuration initialized at: {configService.GetConfigPath()}");
            Console.WriteLine(JsonSerializer.Serialize(cfg, JsonFormatting.Options));
            return 0;
        case "show":
            var loaded = configService.LoadOrCreate();
            Console.WriteLine(JsonSerializer.Serialize(loaded, JsonFormatting.Options));
            return 0;
        case "validate":
            var validateConfig = configService.LoadOrCreate();
            var errors = configService.Validate(validateConfig);
            if (errors.Count == 0)
            {
                Console.WriteLine("Config validation passed.");
                return 0;
            }

            Console.Error.WriteLine("Config validation failed:");
            foreach (var error in errors) Console.Error.WriteLine($" - {error}");
            return 2;
        default:
            Console.Error.WriteLine($"Unknown config subcommand '{sub}'. Use: config init|show|validate");
            return 1;
    }
}

static int HandleReport(string[] args, ConfigService configService, ReportService reportService)
{
    var config = configService.LoadOrCreate();
    DateTimeOffset? fromUtc = null;
    DateTimeOffset? toUtc = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--from":
                if (i + 1 < args.Length && DateTimeOffset.TryParse(args[++i], out var fromParsed)) fromUtc = fromParsed.ToUniversalTime();
                break;
            case "--to":
                if (i + 1 < args.Length && DateTimeOffset.TryParse(args[++i], out var toParsed)) toUtc = toParsed.ToUniversalTime();
                break;
        }
    }

    var report = reportService.CreateAdvancedReport(config.Buffer.Path, fromUtc, toUtc);
    Console.WriteLine(JsonSerializer.Serialize(report, JsonFormatting.Options));
    return 0;
}

static async Task<int> HandleService(string[] args, ConfigService configService, ServiceProbeService probeService)
{
    if (args.Length == 0 || !string.Equals(args[0], "check", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Only 'service check' is currently implemented.");
        return 1;
    }

    var config = configService.LoadOrCreate();
    var result = await probeService.ProbeAsync(config.Service);
    Console.WriteLine(JsonSerializer.Serialize(result, JsonFormatting.Options));
    return result.IsSuccess ? 0 : 2;
}

static async Task<int> HandleSend(string[] args, ConfigService configService, BufferService bufferService, ExportService exportService, bool verbose, bool strict)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Missing send subcommand. Use: send job|log|metric|heartbeat");
        return 1;
    }

    var sub = args[0].ToLowerInvariant();
    var rest = args.Skip(1).ToArray();

    return sub switch
    {
        "job" => await HandleSendJob(rest, configService, bufferService, exportService, verbose, strict),
        "log" => await HandleSendLog(rest, configService, exportService, verbose, strict),
        "metric" => await HandleSendMetric(rest, configService, exportService, verbose, strict),
        "heartbeat" => await HandleSendHeartbeat(rest, configService, exportService, verbose, strict),
        _ => UnknownSend(sub)
    };
}

static int UnknownSend(string sub)
{
    Console.Error.WriteLine($"Unknown send subcommand '{sub}'. Use: send job|log|metric|heartbeat");
    return 1;
}

static async Task<int> HandleSendJob(string[] args, ConfigService configService, BufferService bufferService, ExportService exportService, bool verbose, bool strict)
{
    var parsed = SendJobArgs.Parse(args);
    if (parsed.Event is null)
    {
        foreach (var error in parsed.Errors) Console.Error.WriteLine(error);
        return 1;
    }

    var config = configService.LoadOrCreate();
    var strictMode = strict || config.Agent.StrictMode;

    // Validate the closed-enum fields against the contract: warn + normalize in
    // non-strict mode, reject in strict mode.
    var outcome = JobEventValidator.ValidateAndNormalize(parsed.Event, strictMode);
    foreach (var warning in outcome.Warnings) Console.Error.WriteLine($"warning: {warning}");
    if (outcome.HasErrors)
    {
        foreach (var error in outcome.Errors) Console.Error.WriteLine($"error: {error}");
        return 1;
    }

    var evt = outcome.Event with { FinishedAt = DateTimeOffset.UtcNow };

    var path = bufferService.Enqueue(evt, config.Buffer.Path);
    Console.WriteLine($"Buffered event: {path}");

    try
    {
        var sendResult = await exportService.TrySendAsync(evt, config, verbose);
        if (sendResult.IsSuccess)
        {
            // Confirmed delivery: drop the durable copy so the buffer doesn't leak.
            bufferService.Delete(path);
            Console.WriteLine($"Forwarded event: {sendResult.Message}");
        }
        else if (sendResult.IsSkipped)
        {
            if (verbose) Console.WriteLine($"[verbose] export skipped: {sendResult.Message}");
        }
        else
        {
            Console.Error.WriteLine($"Export failed (event remains buffered): {sendResult.Message}");
            return strictMode ? 3 : 0;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Export failed (event remains buffered): {ex.Message}");
        if (verbose) Console.Error.WriteLine($"[verbose] {ex}");
        return strictMode ? 3 : 0;
    }

    return 0;
}

// Logs and metrics are best-effort: they attempt a direct OTLP export and are
// NOT written to the durable buffer. The RetryWorker only understands JobEvent,
// so wiring these through it would be a half-measure; keeping them best-effort
// is the deliberate, documented scope (see docs/Improvement-Plan.md §7).
static async Task<int> HandleSendLog(string[] args, ConfigService configService, ExportService exportService, bool verbose, bool strict)
{
    var parsed = SendLogArgs.Parse(args);
    if (parsed.Event is null)
    {
        foreach (var error in parsed.Errors) Console.Error.WriteLine(error);
        return 1;
    }

    var config = configService.LoadOrCreate();
    var strictMode = strict || config.Agent.StrictMode;

    var outcome = LogEventValidator.ValidateAndNormalize(parsed.Event, strictMode);
    foreach (var warning in outcome.Warnings) Console.Error.WriteLine($"warning: {warning}");
    if (outcome.HasErrors)
    {
        foreach (var error in outcome.Errors) Console.Error.WriteLine($"error: {error}");
        return 1;
    }

    return await TrySendSignal(
        () => exportService.TrySendLogAsync(outcome.Event, config, verbose),
        "log",
        verbose,
        strictMode);
}

static async Task<int> HandleSendMetric(string[] args, ConfigService configService, ExportService exportService, bool verbose, bool strict)
{
    var parsed = SendMetricArgs.Parse(args);
    if (parsed.Event is null)
    {
        foreach (var error in parsed.Errors) Console.Error.WriteLine(error);
        return 1;
    }

    var config = configService.LoadOrCreate();
    var strictMode = strict || config.Agent.StrictMode;

    var outcome = MetricEventValidator.ValidateAndNormalize(parsed.Event, strictMode);
    foreach (var warning in outcome.Warnings) Console.Error.WriteLine($"warning: {warning}");
    if (outcome.HasErrors)
    {
        foreach (var error in outcome.Errors) Console.Error.WriteLine($"error: {error}");
        return 1;
    }

    return await TrySendSignal(
        () => exportService.TrySendMetricAsync(outcome.Event, config, verbose),
        "metric",
        verbose,
        strictMode);
}

static async Task<int> HandleSendHeartbeat(string[] args, ConfigService configService, ExportService exportService, bool verbose, bool strict)
{
    var interval = 0;
    var count = 1;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--interval" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], out interval) || interval < 0)
                {
                    Console.Error.WriteLine("Invalid --interval (expected non-negative seconds).");
                    return 1;
                }
                break;
            case "--count" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], out count) || count < 1)
                {
                    Console.Error.WriteLine("Invalid --count (expected a positive integer).");
                    return 1;
                }
                break;
        }
    }

    var config = configService.LoadOrCreate();
    var strictMode = strict || config.Agent.StrictMode;
    var instance = config.Agent.InstanceId == "auto" ? Environment.MachineName : config.Agent.InstanceId;
    var worst = 0;

    for (var beat = 0; beat < count; beat++)
    {
        var heartbeat = new MetricEvent
        {
            Name = "telemetrybridge.heartbeat",
            Value = 1,
            Unit = "1",
            Kind = "gauge",
            Instance = instance,
            Attributes = new Dictionary<string, object?> { ["beat"] = (long)(beat + 1) }
        };

        var code = await TrySendSignal(
            () => exportService.TrySendMetricAsync(heartbeat, config, verbose),
            "heartbeat",
            verbose,
            strictMode);
        worst = Math.Max(worst, code);

        if (interval > 0 && beat < count - 1)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval));
        }
    }

    return worst;
}

// Shared best-effort send + reporting for logs/metrics/heartbeats. Mirrors the
// strict-mode exit-code convention used by 'send job'.
static async Task<int> TrySendSignal(Func<Task<ExportResult>> send, string label, bool verbose, bool strictMode)
{
    try
    {
        var result = await send();
        if (result.IsSuccess)
        {
            Console.WriteLine($"Forwarded {label}: {result.Message}");
            return 0;
        }

        if (result.IsSkipped)
        {
            if (verbose) Console.WriteLine($"[verbose] {label} export skipped: {result.Message}");
            return 0;
        }

        Console.Error.WriteLine($"{label} export failed: {result.Message}");
        return strictMode ? 3 : 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{label} export failed: {ex.Message}");
        if (verbose) Console.Error.WriteLine($"[verbose] {ex}");
        return strictMode ? 3 : 0;
    }
}

static async Task<int> HandleBuffer(
    string[] args,
    ConfigService configService,
    BufferService bufferService,
    ExportService exportService,
    bool verbose)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Missing buffer subcommand. Use: buffer status|inspect|flush|retry|purge");
        return 1;
    }

    var config = configService.LoadOrCreate();
    var bufferPath = config.Buffer.Path;
    var deadLetterPath = config.Buffer.DeadLetterPath;
    var sub = args[0].ToLowerInvariant();

    switch (sub)
    {
        case "status":
        {
            var pending = bufferService.List(bufferPath).ToList();
            var dead = bufferService.List(deadLetterPath, deadLetter: true);
            var pendingCount = pending.Count(i => i.State == BufferItemState.Pending);
            var retryingCount = pending.Count(i => i.State == BufferItemState.Retrying);
            var totalBytes = pending.Sum(i => i.SizeBytes) + dead.Sum(i => i.SizeBytes);

            Console.WriteLine("Buffer status:");
            Console.WriteLine($"  pending:     {pendingCount}");
            Console.WriteLine($"  retrying:    {retryingCount}");
            Console.WriteLine($"  dead-letter: {dead.Count}");
            Console.WriteLine($"  total size:  {FormatBytes(totalBytes)}");
            Console.WriteLine($"  buffer path: {bufferPath}");
            return 0;
        }

        case "inspect":
        {
            var items = bufferService.List(bufferPath)
                .Concat(bufferService.List(deadLetterPath, deadLetter: true))
                .ToList();

            if (items.Count == 0)
            {
                Console.WriteLine("Buffer is empty.");
                return 0;
            }

            var now = DateTimeOffset.UtcNow;
            Console.WriteLine($"{"STATE",-11} {"ATTEMPTS",-8} {"AGE",-10} {"NEXT-RETRY",-22} JOB");
            foreach (var item in items)
            {
                var age = FormatDuration(now - item.EnqueuedAt);
                var next = item.NextEligibleAt?.ToString("u") ?? "-";
                var name = string.IsNullOrWhiteSpace(item.Event.JobName) ? "(unnamed)" : item.Event.JobName;
                Console.WriteLine(
                    $"{item.State,-11} {item.Attempts,-8} {age,-10} {next,-22} {name} [{item.Event.Status}]");
            }
            return 0;
        }

        case "flush":
        {
            var worker = new RetryWorker(
                bufferService,
                (evt, ct) => exportService.TrySendAsync(evt, config, verbose, ct),
                BackoffPolicy.Default(),
                RetryOptions.FromConfig(config.Buffer));

            var result = await worker.DrainOnceAsync();
            Console.WriteLine("Buffer flush complete:");
            Console.WriteLine($"  delivered:    {result.Delivered}");
            Console.WriteLine($"  retried:      {result.Retried}");
            Console.WriteLine($"  dead-lettered:{result.DeadLettered}");
            Console.WriteLine($"  dropped:      {result.Dropped}");
            Console.WriteLine($"  skipped:      {result.Skipped}");
            Console.WriteLine($"  not-eligible: {result.NotEligible}");
            return 0;
        }

        case "retry":
        {
            var deadLetterOnly = HasFlag(args, "--dead-letter");
            var all = HasFlag(args, "--all");
            var reset = 0;

            if (all || !deadLetterOnly)
            {
                // Reset retrying items (attempts > 0) back to pending.
                foreach (var item in bufferService.List(bufferPath)
                             .Where(i => i.State == BufferItemState.Retrying))
                {
                    bufferService.ResetToPending(item, bufferPath);
                    reset++;
                }
            }

            if (all || deadLetterOnly)
            {
                foreach (var item in bufferService.List(deadLetterPath, deadLetter: true))
                {
                    bufferService.ResetToPending(item, bufferPath);
                    reset++;
                }
            }

            Console.WriteLine($"Reset {reset} item(s) back to pending.");
            return 0;
        }

        case "purge":
        {
            var deadLetterOnly = HasFlag(args, "--dead-letter");
            var all = HasFlag(args, "--all");
            var purged = 0;

            if (all || !deadLetterOnly)
            {
                foreach (var item in bufferService.List(bufferPath))
                {
                    bufferService.Delete(item.Path);
                    purged++;
                }
            }

            if (all || deadLetterOnly)
            {
                foreach (var item in bufferService.List(deadLetterPath, deadLetter: true))
                {
                    bufferService.Delete(item.Path);
                    purged++;
                }
            }

            Console.WriteLine($"Purged {purged} item(s).");
            return 0;
        }

        default:
            Console.Error.WriteLine($"Unknown buffer subcommand '{sub}'. Use: buffer status|inspect|flush|retry|purge");
            return 1;
    }
}

static async Task<int> HandleDiagnostics(string[] args, ConfigService configService, DiagnosticsService diagnostics, bool verbose)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Missing diagnostics subcommand. Use: diagnostics doctor|network|dns|port|otlp|env|collect");
        return 1;
    }

    var config = configService.LoadOrCreate();
    var (host, port) = SafeParseHostPort(config.Signoz.Endpoint);
    var sub = args[0].ToLowerInvariant();

    switch (sub)
    {
        case "dns":
        {
            var probe = await diagnostics.ResolveDnsAsync(host);
            PrintProbe(probe);
            return probe.Ok ? 0 : 1;
        }

        case "port":
        {
            var probe = await diagnostics.CheckTcpAsync(host, port);
            PrintProbe(probe);
            return probe.Ok ? 0 : 1;
        }

        case "otlp":
        {
            var probe = await diagnostics.CheckOtlpAsync(config);
            PrintProbe(probe);
            return probe.Ok ? 0 : 1;
        }

        case "network":
        {
            Console.WriteLine($"Network diagnostics for {host}:{port}");
            var probes = new[]
            {
                await diagnostics.ResolveDnsAsync(host),
                await diagnostics.CheckTcpAsync(host, port),
                await diagnostics.CheckTlsAsync(host, port)
            };
            foreach (var probe in probes) PrintProbe(probe);
            return probes.Where(p => p.Critical).All(p => p.Ok) ? 0 : 1;
        }

        case "doctor":
        {
            Console.WriteLine($"TelemetryBridge doctor — target {config.Signoz.Endpoint}");
            var probes = new List<ProbeResult>
            {
                await diagnostics.ResolveDnsAsync(host),
                await diagnostics.CheckTcpAsync(host, port),
                await diagnostics.CheckTlsAsync(host, port),
                await diagnostics.CheckOtlpAsync(config)
            };
            var report = DiagnosticsService.BuildReport(probes);
            foreach (var probe in report.Results) PrintProbe(probe);
            Console.WriteLine();
            Console.WriteLine(report.Ok
                ? "Overall: PASS — all critical checks succeeded."
                : "Overall: FAIL — one or more critical checks failed.");
            return report.ExitCode;
        }

        case "env":
            PrintEnv(config, configService);
            return 0;

        case "collect":
            return await HandleCollect(args.Skip(1).ToArray(), config, configService, diagnostics, host, port);

        default:
            Console.Error.WriteLine($"Unknown diagnostics subcommand '{sub}'. Use: diagnostics doctor|network|dns|port|otlp|env|collect");
            return 1;
    }
}

static async Task<int> HandleTest(string[] args, ConfigService configService, DiagnosticsService diagnostics, bool verbose)
{
    if (args.Length == 0 || !string.Equals(args[0], "connection", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Usage: test connection");
        return 1;
    }

    var config = configService.LoadOrCreate();
    Console.WriteLine($"Testing connection to {config.Signoz.Endpoint} ...");

    // The OTLP probe already performs a minimal trace export via ExportService.
    var probe = await diagnostics.CheckOtlpAsync(config);
    PrintProbe(probe);

    if (probe.Ok)
    {
        Console.WriteLine("Connection test PASSED: a minimal trace was exported successfully.");
        return 0;
    }

    Console.Error.WriteLine($"Connection test FAILED: {probe.Detail}");
    return 1;
}

static async Task<int> HandleCollect(
    string[] args,
    BridgeConfig config,
    ConfigService configService,
    DiagnosticsService diagnostics,
    string host,
    int port)
{
    string? outPath = null;
    var zip = false;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
            case "--zip": zip = true; break;
        }
    }

    var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
    var folder = outPath ?? Path.Combine(configService.GetWorkspaceRoot(), "Data", "Support", $"bundle-{stamp}");
    Directory.CreateDirectory(folder);

    // 1. Config snapshot with secrets/headers redacted.
    File.WriteAllText(
        Path.Combine(folder, "config.redacted.json"),
        DiagnosticsService.RedactConfigJson(config));

    // 2. Recent buffer status.
    var bufferService = new BufferService();
    var pending = bufferService.List(config.Buffer.Path);
    var dead = bufferService.List(config.Buffer.DeadLetterPath, deadLetter: true);
    File.WriteAllLines(Path.Combine(folder, "buffer-status.txt"), new[]
    {
        $"pending:     {pending.Count(i => i.State == BufferItemState.Pending)}",
        $"retrying:    {pending.Count(i => i.State == BufferItemState.Retrying)}",
        $"dead-letter: {dead.Count}",
        $"buffer path: {config.Buffer.Path}"
    });

    // 3. Probe results.
    var probes = new List<ProbeResult>
    {
        await diagnostics.ResolveDnsAsync(host),
        await diagnostics.CheckTcpAsync(host, port),
        await diagnostics.CheckTlsAsync(host, port),
        await diagnostics.CheckOtlpAsync(config)
    };
    File.WriteAllText(
        Path.Combine(folder, "probes.json"),
        JsonSerializer.Serialize(probes, ConfigService.JsonOptions));

    var target = folder;
    if (zip)
    {
        var zipPath = folder.TrimEnd(Path.DirectorySeparatorChar) + ".zip";
        if (File.Exists(zipPath)) File.Delete(zipPath);
        System.IO.Compression.ZipFile.CreateFromDirectory(folder, zipPath);
        Directory.Delete(folder, recursive: true);
        target = zipPath;
    }

    Console.WriteLine($"Support bundle written to: {target}");
    return 0;
}

static (string Host, int Port) SafeParseHostPort(string endpoint)
{
    try
    {
        return DiagnosticsService.ParseHostPort(endpoint);
    }
    catch
    {
        // An unparseable/empty endpoint leaves host blank; the probes then fail
        // gracefully with an actionable message instead of throwing.
        return (string.Empty, 0);
    }
}

static void PrintProbe(ProbeResult probe)
{
    var tag = probe.Ok ? "PASS" : probe.Critical ? "FAIL" : "WARN";
    Console.WriteLine($"  [{tag,-4}] {probe.Name,-8} {probe.DurationMs,7:F0}ms  {probe.Detail}");
}

static void PrintEnv(BridgeConfig config, ConfigService configService)
{
    Console.WriteLine("Resolved configuration (secrets redacted):");
    Console.WriteLine(DiagnosticsService.RedactConfigJson(config));
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine($"  machine:        {Environment.MachineName}");
    Console.WriteLine($"  os:             {Environment.OSVersion}");
    Console.WriteLine($"  .net:           {Environment.Version}");
    Console.WriteLine($"  user:           {Environment.UserName}");
    Console.WriteLine($"  cwd:            {Environment.CurrentDirectory}");
    Console.WriteLine($"  config path:    {configService.GetConfigPath()}");
    Console.WriteLine($"  workspace root: {configService.GetWorkspaceRoot()}");
}

static bool HasFlag(string[] args, string flag) =>
    args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

static string FormatBytes(long bytes)
{
    if (bytes < 1024) return $"{bytes} B";
    if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
    return $"{bytes / (1024.0 * 1024.0):F1} MB";
}

static string FormatDuration(TimeSpan span)
{
    if (span < TimeSpan.Zero) span = TimeSpan.Zero;
    if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h{span.Minutes}m";
    if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m{span.Seconds}s";
    return $"{(int)span.TotalSeconds}s";
}

static void PrintHelp(string workspaceRoot)
{
    Console.WriteLine("TelemetryBridge CLI");
    Console.WriteLine("===================");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  telemetrybridge [--verbose|-v] [--strict] <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  config init                  Create default config + local data folders");
    Console.WriteLine("  config show                  Display current config (auto-creates if missing)");
    Console.WriteLine("  config validate              Validate current config");
    Console.WriteLine("  send job --job-name <name> [options]   Buffer + forward a job event");
    Console.WriteLine("      --status <status>          started|succeeded|failed|warning|skipped|");
    Console.WriteLine("                                 cancelled|timeout|interrupted|unknown (default: succeeded)");
    Console.WriteLine("      --system <name>            powershell|scriptrunner|simego-dss|ouvvi|manual");
    Console.WriteLine("      --event-type <type>        job|job-start|job-end (default: job)");
    Console.WriteLine("      --duration-ms <n>          Run duration in milliseconds");
    Console.WriteLine("      --exit-code <n>            Process exit code");
    Console.WriteLine("      --correlation-id <id>      Correlation id");
    Console.WriteLine("      --parent-correlation-id <id>  Parent correlation id");
    Console.WriteLine("      --instance <name>          Originating instance/host");
    Console.WriteLine("      --script-path <path>       Script path");
    Console.WriteLine("      --script-name <name>       Script name");
    Console.WriteLine("      --script-version <ver>     Script version");
    Console.WriteLine("      --runbook <name>           Runbook name");
    Console.WriteLine("      --environment <name>       Environment name");
    Console.WriteLine("      --error-message <text>     Structured error message");
    Console.WriteLine("      --error-type <type>        Structured error type");
    Console.WriteLine("      --error-code <code>        Structured error code");
    Console.WriteLine("      --attr <key=value>         Free-form attribute (repeatable)");
    Console.WriteLine();
    Console.WriteLine("  send log --message <text> [options]    Export a log record (best-effort, not buffered)");
    Console.WriteLine("      --severity <level>         trace|debug|info|warn|error|fatal (default: info)");
    Console.WriteLine("      --correlation-id <id>      Correlation id");
    Console.WriteLine("      --instance <name>          Originating instance/host");
    Console.WriteLine("      --attr <key=value>         Free-form attribute (repeatable)");
    Console.WriteLine("  send metric --name <name> --value <n> [options]  Export a metric (best-effort, not buffered)");
    Console.WriteLine("      --unit <unit>              Unit of measure (e.g. ms, items)");
    Console.WriteLine("      --kind <kind>              counter|gauge (default: gauge)");
    Console.WriteLine("      --correlation-id <id>      Correlation id");
    Console.WriteLine("      --instance <name>          Originating instance/host");
    Console.WriteLine("      --attr <key=value>         Free-form attribute (repeatable)");
    Console.WriteLine("  send heartbeat [--interval <s>] [--count <n>]    Emit liveness heartbeat metric(s)");
    Console.WriteLine();
    Console.WriteLine("  buffer status                Show pending / retrying / dead-letter counts + size");
    Console.WriteLine("  buffer inspect               List buffered items with key fields");
    Console.WriteLine("  buffer flush                 Run one drain pass now (retry + dead-letter)");
    Console.WriteLine("  buffer retry [--dead-letter|--all]   Reset retrying/dead-letter items to pending");
    Console.WriteLine("  buffer purge [--dead-letter|--all]   Delete buffered and/or dead-letter items");
    Console.WriteLine();
    Console.WriteLine("  diagnostics doctor           Run all probes + aggregated verdict (exit code reflects result)");
    Console.WriteLine("  diagnostics network          DNS + TCP + TLS probes against the configured endpoint");
    Console.WriteLine("  diagnostics dns              Resolve the endpoint host via DNS");
    Console.WriteLine("  diagnostics port             TCP-connect to the endpoint host:port");
    Console.WriteLine("  diagnostics otlp             Try a minimal OTLP export to the endpoint");
    Console.WriteLine("  diagnostics env              Print resolved config (redacted) + environment info");
    Console.WriteLine("  diagnostics collect [--out <path>] [--zip]   Write a redacted support bundle");
    Console.WriteLine();
    Console.WriteLine("  test connection              OTLP probe + minimal trace export, pass/fail report");
    Console.WriteLine();
    Console.WriteLine("  report [--from <ISO-8601>] [--to <ISO-8601>]  Advanced reporting over buffered events");
    Console.WriteLine("  service check                Probe local HTTP/TCP endpoints from config");
    Console.WriteLine();
    Console.WriteLine("Global options:");
    Console.WriteLine("  --verbose, -v                Verbose diagnostic output");
    Console.WriteLine("  --strict                     Exit non-zero when telemetry export fails");
    Console.WriteLine();
    Console.WriteLine("Local workspace:");
    Console.WriteLine($"  {workspaceRoot}");
    Console.WriteLine("  ├─ telemetrybridge.config.json");
    Console.WriteLine("  └─ Data/");
    Console.WriteLine("     ├─ Buffer/");
    Console.WriteLine("     └─ DeadLetter/");
}

static class JsonFormatting
{
    // Mirror the on-disk camelCase format used by ConfigService.
    public static readonly JsonSerializerOptions Options = ConfigService.JsonOptions;
}
