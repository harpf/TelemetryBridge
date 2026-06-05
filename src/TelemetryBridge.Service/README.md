# TelemetryBridge.Service

A .NET 10 Worker Service (`Microsoft.Extensions.Hosting`) that hosts the
TelemetryBridge background loop:

- **Retry / drain loop** — on `buffer.flushIntervalSeconds` it runs one
  `RetryWorker` drain pass over the durable buffer (deliver → delete on success,
  exponential backoff + jitter on failure, dead-letter once attempts/age are
  exhausted). The OTLP `TracerProvider`/exporter is built **once** and reused for
  the lifetime of the service (the perf win the improvement plan calls out — the
  CLI rebuilds it per event).
- **Localhost HTTP ingest** *(optional)* — `POST` a `JobEvent` JSON and it is
  validated and enqueued on the durable buffer, returning `202 Accepted`. Bound
  to loopback only.
- **Health + heartbeat** — `GET /health` returns the live buffer status; a
  heartbeat line is logged on an interval.
- **Graceful shutdown** — on stop it runs one final, time-bounded drain so
  in-flight items get a last delivery attempt.

## Configuration

The host reads the shared `telemetrybridge.config.json` (same file the CLI uses),
resolved in this order:

1. `--config <path>` command-line argument
2. `TELEMETRYBRIDGE_CONFIG` environment variable
3. `ConfigService` default (`<CurrentDirectory>/TelemetryBridge/telemetrybridge.config.json`)

If no config exists, a default one (and its data folders) is created on first run.

The drain loop, buffer, dead-letter and OTLP/SigNoz settings come from the
standard `buffer` / `signoz` sections. The host adds three keys under `agent`:

| Key | Default | Meaning |
| --- | --- | --- |
| `agent.enableHttpIngest` | `false` | Start the localhost HTTP ingest + health endpoint |
| `agent.listenUrl` | `http://localhost:5050` | Loopback URL the endpoint binds to (must be loopback) |
| `agent.heartbeatIntervalSeconds` | `60` | Heartbeat log cadence |

> These three are read directly from the config JSON (they are not yet modelled
> on `BridgeConfig.AgentOptions` in Core, which a sibling workspace owns). They
> can also be overridden via `TELEMETRYBRIDGE_ENABLE_HTTP_INGEST` and
> `TELEMETRYBRIDGE_LISTEN_URL`.

Example `agent` block:

```json
{
  "agent": {
    "instanceId": "auto",
    "strictMode": false,
    "enableHttpIngest": true,
    "listenUrl": "http://localhost:5050",
    "heartbeatIntervalSeconds": 60
  }
}
```

## Run it (console)

```powershell
# from the repo root
dotnet run --project src/TelemetryBridge.Service -- --config C:\ProgramData\Levitec\AutomationTelemetry\telemetrybridge.config.json

# or run the published/built DLL directly
dotnet src/TelemetryBridge.Service/bin/Debug/net10.0/TelemetryBridge.Service.dll --config <path>
```

With `agent.enableHttpIngest = true`:

```powershell
# health
curl http://localhost:5050/health

# ingest a job event
curl -X POST http://localhost:5050/ingest `
  -H "Content-Type: application/json" `
  -d '{"jobName":"nightly-sync","status":"succeeded","system":"powershell"}'
# -> 202 Accepted, event is durably buffered and delivered by the drain loop
```

## Install as a Windows service

The host already calls `AddWindowsService(...)`, so the published executable can
be registered with `sc.exe`. **This repo does not ship an installer** — these are
the manual commands.

1. Publish a self-contained or framework-dependent build:

   ```powershell
   dotnet publish src/TelemetryBridge.Service -c Release -o C:\Program Files\TelemetryBridge
   ```

2. Create the service (note the quoting; `binPath` points at the published EXE and
   passes the config path as an argument):

   ```powershell
   sc.exe create TelemetryBridge `
     binPath= "\"C:\Program Files\TelemetryBridge\TelemetryBridge.Service.exe\" --config \"C:\ProgramData\Levitec\AutomationTelemetry\telemetrybridge.config.json\"" `
     start= auto `
     DisplayName= "TelemetryBridge"
   sc.exe description TelemetryBridge "Durable telemetry buffer + retry + localhost ingest for automation jobs."
   ```

   > If you published framework-dependent without an apphost, set `binPath` to
   > `"dotnet \"C:\Program Files\TelemetryBridge\TelemetryBridge.Service.dll\" --config \"<path>\""` instead.

3. Start / stop / inspect / remove:

   ```powershell
   sc.exe start   TelemetryBridge
   sc.exe query   TelemetryBridge
   sc.exe stop    TelemetryBridge
   sc.exe delete  TelemetryBridge
   ```

Logs go to stdout (captured by the SCM / your log pipeline). The heartbeat line
and per-drain summary make it easy to confirm the queue is draining.

## Tests

`tests/TelemetryBridge.Service.Tests` covers the background drain trigger + final
flush on shutdown, the `RetryWorker` drain delivering buffered items, HTTP ingest
validation/enqueue (including strict-mode rejection), config parsing of the
`agent.*` host keys, loopback-only binding, and an end-to-end `HttpListener`
round-trip against `/health` + `/ingest`.

```powershell
dotnet test tests/TelemetryBridge.Service.Tests
```
