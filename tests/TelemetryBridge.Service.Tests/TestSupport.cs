using System.Diagnostics;
using TelemetryBridge.Core.Models;
using TelemetryBridge.Core.Services;
using TelemetryBridge.Service.Configuration;
using TelemetryBridge.Service.Hosting;

namespace TelemetryBridge.Service.Tests;

/// <summary>Shared fixtures: temp buffers, option builders, and async polling.</summary>
internal static class TestSupport
{
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tbsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static ServiceHostOptions Options(
        string? bufferPath = null,
        int flushSeconds = 30,
        bool strict = false,
        bool enableIngest = false,
        string listenUrl = ServiceHostOptions.DefaultListenUrl)
    {
        bufferPath ??= NewTempDir();
        var bridge = new BridgeConfig
        {
            Agent = new BridgeConfig.AgentOptions { StrictMode = strict },
            Buffer = new BridgeConfig.BufferOptions
            {
                Enabled = true,
                Path = bufferPath,
                DeadLetterPath = Path.Combine(bufferPath, "dead-letter"),
                FlushIntervalSeconds = flushSeconds,
                MaxSizeMb = 500
            }
        };

        return new ServiceHostOptions
        {
            Bridge = bridge,
            ConfigPath = "test.config.json",
            EnableHttpIngest = enableIngest,
            ListenUrl = listenUrl,
            HeartbeatIntervalSeconds = 60
        };
    }

    public static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string? because = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException(because ?? "Condition was not met in time.");
            }
            await Task.Delay(20);
        }
    }
}

/// <summary>Records drain invocations so the background loop can be tested without networking.</summary>
internal sealed class FakeDrainer : IBufferDrainer
{
    private int _count;

    public int Count => Volatile.Read(ref _count);
    public DrainResult Result { get; set; } = new();
    public Func<CancellationToken, Task>? OnDrain { get; set; }

    public async Task<DrainResult> DrainOnceAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        if (OnDrain is not null) await OnDrain(cancellationToken);
        return Result;
    }
}
