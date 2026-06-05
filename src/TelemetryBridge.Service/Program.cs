using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Configuration;
using TelemetryBridge.Service.Health;
using TelemetryBridge.Service.Hosting;
using TelemetryBridge.Service.Ingest;
using TelemetryBridge.Service.Telemetry;

// Resolve config + host options once, before the host is built, so a bad config
// fails fast with a clear message instead of deep inside DI.
var configService = new ConfigService();
var configOverride = ResolveConfigArg(args);

ServiceHostOptions options;
try
{
    options = ServiceHostOptions.Load(configService, configOverride);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load TelemetryBridge config: {ex.Message}");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// Let the host run as a Windows service (no-op when launched from a console).
builder.Services.AddWindowsService(o => o.ServiceName = options.Bridge.Service.Name);

// Singletons: config-derived options, the buffer, and — the key perf win — a
// single hoisted OTLP exporter/provider reused for every drain pass.
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.Bridge);
builder.Services.AddSingleton<BufferService>(_ => new BufferService());
builder.Services.AddSingleton<IJobEventExporter>(sp => new SingletonTraceExporter(options.Bridge));
builder.Services.AddSingleton<IBufferDrainer>(sp => new RetryWorkerDrainer(
    sp.GetRequiredService<BufferService>(),
    sp.GetRequiredService<IJobEventExporter>(),
    options));
builder.Services.AddSingleton<HealthReporter>();
builder.Services.AddSingleton<JobEventIngestHandler>();

// Hosted services: the retry/drain loop, the heartbeat log, and (optionally) the
// localhost HTTP ingest + health endpoint.
builder.Services.AddHostedService<DrainWorker>();
builder.Services.AddHostedService<HeartbeatWorker>();
builder.Services.AddHostedService<HttpIngestServer>();

var host = builder.Build();

host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("TelemetryBridge.Service")
    .LogInformation(
        "TelemetryBridge service host starting. config={ConfigPath} endpoint={Endpoint} " +
        "flushInterval={FlushSeconds}s httpIngest={Ingest} listenUrl={ListenUrl}.",
        options.ConfigPath, options.Bridge.Signoz.Endpoint, options.FlushInterval.TotalSeconds,
        options.EnableHttpIngest, options.ListenUrl);

await host.RunAsync();
return 0;

// Supports `--config <path>` / `--config=<path>` so a service install can point
// at an explicit machine-wide config file.
static string? ResolveConfigArg(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return args[i + 1];
        }

        const string inline = "--config=";
        if (args[i].StartsWith(inline, StringComparison.OrdinalIgnoreCase))
        {
            return args[i][inline.Length..];
        }
    }

    return null;
}
