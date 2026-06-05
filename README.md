# TelemetryBridge

[![CI](https://github.com/harpf/TelemetryBridge/actions/workflows/ci.yml/badge.svg)](https://github.com/harpf/TelemetryBridge/actions/workflows/ci.yml)

TelemetryBridge is a **local-first automation telemetry bridge** for Windows automation servers. It is intended to be built with **.NET 10** and deployed side-by-side with PowerShell scripts, ScriptRunner actions, and Simego DSS/Ouvvi jobs.

## Problem it solves
Automation scripts should not directly depend on SigNoz availability or network health. TelemetryBridge accepts local telemetry calls, persists data durably, and forwards to SigNoz through OTLP when connectivity is available.

## Target architecture
```text
Scripts/Jobs -> Local CLI/HTTP -> TelemetryBridge -> OTLP -> SigNoz (On-Prem)
                                   \-> Durable Buffer + Retry
```

## Current CLI capabilities
- `config init|show|validate` for workspace and configuration management.
- `send job` with rich script/runbook metadata and custom attributes.
- `report` for advanced reporting over buffered telemetry (including time filters).
- `service check` to probe local HTTP and TCP dependencies via config.

## Configuration overview
`telemetrybridge.config.json` now includes:
- `agent` settings (instance mode, strict mode)
- `signoz` OTLP settings (`endpoint`, `protocol`, `timeoutSeconds`, headers)
- `buffer` settings (`path`, `maxSizeMb`)
- `service` settings for local dependency checks:
  - `enabled`
  - `httpEndpoint`
  - `tcpHost`
  - `tcpPort`
  - `timeoutSeconds`

## Examples

### 1) Initialize and validate config
```bash
telemetrybridge config init
telemetrybridge config validate
telemetrybridge config show
```

### 2) Send detailed runbook/script telemetry
```bash
telemetrybridge send job \
  --job-name "Nightly-ERP-Sync" \
  --status failed \
  --duration-ms 18234 \
  --system powershell \
  --runbook "OUVVI-Nightly" \
  --script-path "C:\\Ops\\Runbooks\\NightlyErpSync.ps1" \
  --environment "prod" \
  --attr tenant=de \
  --attr region=eu-central \
  --attr retryCount=2
```

### 3) Generate advanced reports from buffered events
```bash
# Full report over all buffered events
telemetrybridge report

# Time-window report (ISO-8601)
telemetrybridge report --from 2026-05-01T00:00:00Z --to 2026-05-04T23:59:59Z
```

The advanced report includes:
- total events
- succeeded / failed counts
- duration statistics (average, p50, p95, max)
- grouping by status and source system
- top failed jobs

### 4) Check local endpoints/services
```bash
telemetrybridge service check
```

This command evaluates configured local dependencies and returns structured JSON with:
- overall `isSuccess`
- HTTP check result (status/message)
- TCP check result (connectivity/message)

## Repository layout
- `docs/MVP-Plan.md` — implementation plan and component architecture.
- `docs/Event-Contract.json` — JSON schema draft for job events.
- `examples/config.sample.json` — baseline configuration.
- `src/TelemetryBridge.Cli` — CLI host.
- `src/TelemetryBridge.Core` — domain models and services.
- `src/TelemetryBridge.Service` — planned Windows service host.
- `src/TelemetryBridge.PowerShell` — planned PowerShell module.

## Next implementation steps
1. Add localhost HTTP ingest endpoint for script-friendly event submission.
2. Implement retry worker + dead-letter queue management.
3. Add endpoint diagnostics command family (`dns`, `port`, `otlp`).
4. Add export backpressure/buffer retention policy checks.
5. Extend test coverage for report generation and service probes.
