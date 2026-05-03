using System.Globalization;
using System.Text.Json;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

var argsList = args;
var configService = new ConfigService();
var bufferService = new BufferService();

if (argsList.Length == 0 || argsList[0] is "-h" or "--help" or "help")
{
    PrintHelp(configService.GetWorkspaceRoot());
    return 0;
}

var cmd = argsList[0].ToLowerInvariant();

return cmd switch
{
    "config" => HandleConfig(argsList.Skip(1).ToArray(), configService),
    "send" => HandleSend(argsList.Skip(1).ToArray(), configService, bufferService),
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

static int HandleSend(string[] args, ConfigService configService, BufferService bufferService)
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

    var path = bufferService.Enqueue(evt, config.Buffer.Path);
    Console.WriteLine($"Buffered event: {path}");
    return 0;
}

static void PrintHelp(string workspaceRoot)
{
    Console.WriteLine("TelemetryBridge CLI");
    Console.WriteLine("===================");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  telemetrybridge <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  config init                  Create default config + local data folders");
    Console.WriteLine("  config show                  Display current config (auto-creates if missing)");
    Console.WriteLine("  config validate              Validate current config");
    Console.WriteLine("  send job --job-name <name> [--status <status>] [--duration-ms <n>] [--system <name>]");
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
