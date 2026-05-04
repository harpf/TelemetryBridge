using System.Globalization;
using System.Text.Json;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

var argsList = args;
var configService = new ConfigService();
var bufferService = new BufferService();
var exportService = new ExportService();
var reportService = new ReportService();
var serviceProbeService = new ServiceProbeService();

if (argsList.Length == 0 || argsList[0] is "-h" or "--help" or "help")
{
    PrintHelp(configService.GetWorkspaceRoot());
    return 0;
}

var verbose = argsList.Any(a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase));
argsList = argsList.Where(a => !string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase) && !string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase)).ToArray();

var cmd = argsList[0].ToLowerInvariant();

return cmd switch
{
    "config" => HandleConfig(argsList.Skip(1).ToArray(), configService),
    "send" => await HandleSend(argsList.Skip(1).ToArray(), configService, bufferService, exportService, verbose),
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

static async Task<int> HandleSend(string[] args, ConfigService configService, BufferService bufferService, ExportService exportService, bool verbose)
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
    string? runbook = null;
    string? scriptPath = null;
    string? environmentName = null;
    var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--job-name": if (i + 1 < args.Length) jobName = args[++i]; break;
            case "--status": if (i + 1 < args.Length) status = args[++i]; break;
            case "--duration-ms":
                if (i + 1 < args.Length && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) durationMs = parsed;
                break;
            case "--system": if (i + 1 < args.Length) system = args[++i]; break;
            case "--runbook": if (i + 1 < args.Length) runbook = args[++i]; break;
            case "--script-path": if (i + 1 < args.Length) scriptPath = args[++i]; break;
            case "--environment": if (i + 1 < args.Length) environmentName = args[++i]; break;
            case "--attr":
                if (i + 1 < args.Length)
                {
                    var pair = args[++i].Split('=', 2);
                    if (pair.Length == 2) attributes[pair[0]] = pair[1];
                }
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
        RunbookName = runbook,
        ScriptPath = scriptPath,
        EnvironmentName = environmentName,
        HostName = Environment.MachineName,
        Attributes = attributes,
        FinishedAt = DateTimeOffset.UtcNow
    };

    var path = bufferService.Enqueue(evt, config.Buffer.Path);
    Console.WriteLine($"Buffered event: {path}");

    var sendResult = await exportService.TrySendAsync(evt, config, verbose);
    if (sendResult.IsSuccess) Console.WriteLine($"Forwarded event: {sendResult.Message}");
    else if (!sendResult.IsSkipped) Console.Error.WriteLine($"Export failed (event remains buffered): {sendResult.Message}");

    return 0;
}

static void PrintHelp(string workspaceRoot)
{
    Console.WriteLine("TelemetryBridge CLI");
    Console.WriteLine("===================");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  telemetrybridge [--verbose|-v] <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  config init                  Create default config + local data folders");
    Console.WriteLine("  config show                  Display current config (auto-creates if missing)");
    Console.WriteLine("  config validate              Validate current config");
    Console.WriteLine("  send job --job-name <name> [--status <status>] [--duration-ms <n>] [--system <name>] [--runbook <name>] [--script-path <path>] [--environment <name>] [--attr k=v]");
    Console.WriteLine("  report [--from <ISO-8601>] [--to <ISO-8601>]  Advanced reporting from buffered events");
    Console.WriteLine("  service check                Probe local HTTP/TCP endpoints from config");
    Console.WriteLine();
    Console.WriteLine("Local workspace:");
    Console.WriteLine($"  {workspaceRoot}");
    Console.WriteLine("  ├─ telemetrybridge.config.json");
    Console.WriteLine("  └─ Data/");
    Console.WriteLine("     └─ Buffer/");
}

static class JsonFormatting
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
