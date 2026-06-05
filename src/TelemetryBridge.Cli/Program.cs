using System.Globalization;
using System.Text.Json;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

var argsList = args;
var configService = new ConfigService();
var bufferService = new BufferService();
var exportService = new ExportService();

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

static async Task<int> HandleSend(string[] args, ConfigService configService, BufferService bufferService, ExportService exportService, bool verbose, bool strict)
{
    if (args.Length == 0 || !string.Equals(args[0], "job", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Only 'send job' is currently implemented.");
        return 1;
    }

    string? jobName = null;
    var status = "succeeded";
    double? durationMs = null;
    var system = "powershell";

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--job-name":
                if (i + 1 < args.Length) jobName = args[++i];
                break;
            case "--status":
                if (i + 1 < args.Length) status = args[++i];
                break;
            case "--duration-ms":
                if (i + 1 < args.Length && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    durationMs = parsed;
                }
                break;
            case "--system":
                if (i + 1 < args.Length) system = args[++i];
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(jobName))
    {
        Console.Error.WriteLine("--job-name is required.");
        return 1;
    }

    var config = configService.LoadOrCreate();
    var evt = new JobEvent
    {
        JobName = jobName,
        Status = status,
        DurationMs = durationMs,
        System = system,
        FinishedAt = DateTimeOffset.UtcNow
    };

    var strictMode = strict || config.Agent.StrictMode;

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
    Console.WriteLine("  send job --job-name <name> [--status <status>] [--duration-ms <n>] [--system <name>]");
    Console.WriteLine();
    Console.WriteLine("  buffer status                Show pending / retrying / dead-letter counts + size");
    Console.WriteLine("  buffer inspect               List buffered items with key fields");
    Console.WriteLine("  buffer flush                 Run one drain pass now (retry + dead-letter)");
    Console.WriteLine("  buffer retry [--dead-letter|--all]   Reset retrying/dead-letter items to pending");
    Console.WriteLine("  buffer purge [--dead-letter|--all]   Delete buffered and/or dead-letter items");
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
