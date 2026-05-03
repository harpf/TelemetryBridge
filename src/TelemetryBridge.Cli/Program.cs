using System.Text.Json;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;

var argsList = args.ToList();
var configService = new ConfigService();
var bufferService = new BufferService();

if (argsList.Count == 0)
{
    PrintHelp();
    return 0;
}

var cmd = argsList[0].ToLowerInvariant();

switch (cmd)
{
    case "config":
        return HandleConfig(argsList.Skip(1).ToList(), configService);
    case "send":
        return HandleSend(argsList.Skip(1).ToList(), configService, bufferService);
    default:
        Console.Error.WriteLine($"Unknown command '{cmd}'.");
        PrintHelp();
        return 1;
}

static int HandleConfig(List<string> args, ConfigService configService)
{
    if (args.Count == 0) { Console.WriteLine("Missing subcommand."); return 1; }

    var sub = args[0].ToLowerInvariant();
    switch (sub)
    {
        case "init":
            var cfg = configService.Init();
            Console.WriteLine("Config initialized.");
            Console.WriteLine(JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        case "show":
            var loaded = configService.Load();
            Console.WriteLine(JsonSerializer.Serialize(loaded, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        case "validate":
            var validateConfig = configService.Load();
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
            Console.Error.WriteLine($"Unknown config subcommand '{sub}'.");
            return 1;
    }
}

static int HandleSend(List<string> args, ConfigService configService, BufferService bufferService)
{
    if (args.Count == 0 || args[0].ToLowerInvariant() != "job")
    {
        Console.Error.WriteLine("Only 'send job' is currently implemented.");
        return 1;
    }

    string? jobName = null;
    string status = "succeeded";
    double? durationMs = null;
    string system = "powershell";

    for (var i = 1; i < args.Count; i++)
    {
        switch (args[i])
        {
            case "--job-name": jobName = i + 1 < args.Count ? args[++i] : null; break;
            case "--status": status = i + 1 < args.Count ? args[++i] : status; break;
            case "--duration-ms":
                if (i + 1 < args.Count && double.TryParse(args[++i], out var parsed)) durationMs = parsed;
                break;
            case "--system": system = i + 1 < args.Count ? args[++i] : system; break;
        }
    }

    if (string.IsNullOrWhiteSpace(jobName))
    {
        Console.Error.WriteLine("--job-name is required.");
        return 1;
    }

    var config = configService.Load();
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
    Console.WriteLine("Export pipeline not yet implemented (MVP phase 2+).");
    return 0;
}

static void PrintHelp()
{
    Console.WriteLine("TelemetryBridge CLI (initial implementation)");
    Console.WriteLine("Commands:");
    Console.WriteLine("  config init|show|validate");
    Console.WriteLine("  send job --job-name <name> [--status <status>] [--duration-ms <n>] [--system <name>]");
}
