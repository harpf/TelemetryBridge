# TelemetryBridge.Cli

Lightweight CLI for `TelemetryBridge` with local-first behavior.

## Quick start

### 1) Show help
```powershell
TelemetryBridge.Cli.exe --help
```

### 2) Initialize local workspace + config
```powershell
TelemetryBridge.Cli.exe config init
```
This creates:
- `./TelemetryBridge/telemetrybridge.config.json`
- `./TelemetryBridge/Data/Buffer/`

### 3) Show current config
```powershell
TelemetryBridge.Cli.exe config show
```
If the config does not exist yet, it is created automatically.

### 4) Validate config
```powershell
TelemetryBridge.Cli.exe config validate
```

### 5) Send a job event
```powershell
TelemetryBridge.Cli.exe send job --job-name "NightlyBackup" --status succeeded --duration-ms 1523.7 --system powershell
```

### 6) Minimal send example
```powershell
TelemetryBridge.Cli.exe send job --job-name "ImportCustomers"
```

## Command reference

```text
config init
config show
config validate
send job --job-name <name> [--status <status>] [--duration-ms <n>] [--system <name>]
```

## Notes
- `--job-name` is required for `send job`.
- `--duration-ms` expects a numeric value (e.g. `1234.5`).
- Telemetry events are buffered to disk in `TelemetryBridge/Data/Buffer`.

## Planned technical stack
- `System.CommandLine` for command tree.
- `Microsoft.Extensions.Hosting` for dependency injection/config/logging.
- Shared domain abstractions from `TelemetryBridge.Core`.

## Boot flow (target)
1. Load default + local config.
2. Parse command.
3. Build event payload.
4. Write durable buffer record.
5. Attempt immediate export.
6. Return command result with strict/non-strict semantics.
