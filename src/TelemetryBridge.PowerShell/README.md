# TelemetryBridge.PowerShell

An ergonomic PowerShell wrapper over the TelemetryBridge CLI — the primary entry
point for PowerShell / ScriptRunner automation users. Wrap any job in
`Invoke-WithTelemetry` to record its outcome, duration, and errors **without
changing the job's own success/failure**.

This is a thin shim: it shells out to the built CLI (`send job`, `buffer status`)
and contains no telemetry logic of its own.

## Requirements

- PowerShell 5.1+ or PowerShell 7+ (Core or Desktop).
- The TelemetryBridge CLI, reachable via one of (checked in this order):
  1. the `-CliPath` parameter,
  2. the `TELEMETRYBRIDGE_CLI` environment variable,
  3. `telemetrybridge` (or `telemetrybridge.exe`) on `PATH`,
  4. a locally built artifact under `src/TelemetryBridge.Cli/bin` (relative to the
     module). A `.dll` is launched via `dotnet <dll>`; a native `.exe` directly.

  If none resolve, the cmdlets throw a clear message explaining the options.

## Install / import

```powershell
Import-Module ./src/TelemetryBridge.PowerShell/TelemetryBridge.psd1 -Force
Get-Command -Module TelemetryBridge
# Get-TelemetryBufferStatus
# Invoke-WithTelemetry
# Send-TelemetryJob
```

Point the module at your CLI if it isn't on `PATH`:

```powershell
$env:TELEMETRYBRIDGE_CLI = 'C:\Tools\TelemetryBridge\TelemetryBridge.Cli.dll'
# or per-call:  Invoke-WithTelemetry ... -CliPath 'C:\Tools\...\TelemetryBridge.Cli.exe'
```

## Cmdlets

### `Invoke-WithTelemetry` — wrap a job

Runs a script block, times it, and reports `succeeded` / `failed` (with the error
message) to TelemetryBridge. The wrapped block's **output and outcome are always
preserved**: its output is returned, and a failure is re-thrown unchanged after
telemetry is recorded.

```powershell
# Telemetry is best-effort by default — a telemetry failure never breaks the job.
Invoke-WithTelemetry -JobName 'Sync-Users' -System 'powershell' -ScriptBlock {
    .\Sync-Users.ps1
}
```

With attributes and correlation:

```powershell
Invoke-WithTelemetry -JobName 'Nightly-ETL' `
    -Attributes @{ env = 'prod'; region = 'eu' } `
    -CorrelationId (New-Guid) `
    -ScriptName 'Nightly-ETL.ps1' `
    -ScriptBlock { Invoke-Etl }
```

Capturing the wrapped output:

```powershell
$rows = Invoke-WithTelemetry -JobName 'Export-Report' -ScriptBlock {
    Get-Report | Export-Csv .\report.csv -PassThru
}
# $rows is exactly what the script block produced.
```

**Strict mode.** By default telemetry never affects the job. With `-Strict`, the
CLI is told to fail on export errors and a telemetry export failure is surfaced —
but **only when the wrapped block itself succeeded**. If the block failed, its
error always wins:

```powershell
Invoke-WithTelemetry -JobName 'Critical-Job' -Strict -ScriptBlock { Do-Work }
```

Key parameters: `-JobName` (required), `-ScriptBlock` (required), `-System`,
`-Strict`, `-Attributes`, `-CorrelationId`, `-ParentCorrelationId`, `-Instance`
(defaults to the machine name), `-ScriptName`, `-ScriptPath`, `-EventType`,
`-CliPath`.

### `Send-TelemetryJob` — direct `send job`

A typed passthrough to `telemetrybridge send job` for when you already know the
outcome and just want to emit an event.

```powershell
Send-TelemetryJob -JobName 'Sync-Users' -Status succeeded -DurationMs 1234 `
    -Attributes @{ rows = '512' }

Send-TelemetryJob -JobName 'Sync-Users' -Status failed `
    -ErrorMessage 'connection timed out' -ErrorType 'TimeoutException' -Strict
```

`-Status` is validated against the contract enum (`started`, `succeeded`,
`failed`, `warning`, `skipped`, `cancelled`, `timeout`, `interrupted`,
`unknown`). In non-strict mode the CLI returns success even when the export
fails (the event stays durably buffered for retry), so this cmdlet only throws on
a real failure — an argument/config error, or an export failure under `-Strict`.

### `Get-TelemetryBufferStatus` — `buffer status`

```powershell
Get-TelemetryBufferStatus
# Buffer status:
#   pending:     0
#   retrying:    0
#   dead-letter: 0
#   ...
```

## Tests

Pester v5 tests live in `tests/TelemetryBridge.PowerShell.Tests`. They mock the
single CLI seam (`Invoke-TelemetryBridgeCli`) and assert on the exact argument
vector, so no real CLI is launched.

```powershell
Import-Module Pester -MinimumVersion 5.0.0
$config = New-PesterConfiguration
$config.Run.Path = './tests/TelemetryBridge.PowerShell.Tests'
Invoke-Pester -Configuration $config
```
