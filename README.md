# TelemetryBridge

TelemetryBridge is a **local-first automation telemetry bridge** for Windows automation servers. It is intended to be built with **.NET 10** and deployed side-by-side with PowerShell scripts, ScriptRunner actions, and Simego DSS/Ouvvi jobs.

## Problem it solves
Automation scripts should not directly depend on SigNoz availability or network health. TelemetryBridge accepts local telemetry calls, persists data durably, and forwards to SigNoz through OTLP when connectivity is available.

## Target architecture
```text
Scripts/Jobs -> Local CLI/HTTP -> TelemetryBridge -> OTLP -> SigNoz (On-Prem)
                                   \-> Durable Buffer + Retry
```

## MVP objectives
- CLI command surface for config, send, test, diagnostics, and buffer operations.
- Persistent disk-backed queue resilient across server restarts.
- OTLP export using gRPC (`:4317`) and HTTP (`:4318`).
- Diagnostics (`doctor`, `network`, `dns`, `port`, `otlp`, `buffer`, `env`, `collect`).
- Non-strict mode by default so telemetry failures do not fail production scripts.

## Repository layout
- `docs/MVP-Plan.md` — implementation plan and component architecture.
- `docs/Event-Contract.json` — JSON schema draft for job events.
- `examples/config.sample.json` — baseline configuration.
- `src/TelemetryBridge.Cli` — planned CLI host.
- `src/TelemetryBridge.Core` — planned domain abstractions.
- `src/TelemetryBridge.Service` — planned Windows service host.
- `src/TelemetryBridge.PowerShell` — planned PowerShell module.

## Next implementation steps
1. Scaffold .NET 10 solution and projects.
2. Implement `config init/show/validate` and strong typed config binding.
3. Implement durable buffer write path before export.
4. Implement `test connection` and OTLP probe.
5. Implement `send job`, `send job-start`, `send job-end` contracts.
6. Add retry worker + dead-letter queue.
7. Add service hosting and localhost ingest endpoint.
