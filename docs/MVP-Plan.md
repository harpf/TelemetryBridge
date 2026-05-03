# TelemetryBridge MVP Plan (.NET 10)

## Scope
This document translates the target architecture into an implementable MVP backlog for **TelemetryBridge**.

## Product shape
- Local-first telemetry bridge per automation server.
- Ingest via CLI (MVP) and optional localhost HTTP endpoint (phase 6).
- Durable spool before export to OTLP endpoint.
- Retry worker with exponential backoff and dead-letter handling.
- Diagnostics commands for network/OTLP/buffer health.

## MVP command surface
```text
levitec-telemetry
├─ config: init|show|set|get|validate|export
├─ test: connection|send-trace|send-log|send-metric
├─ send: job|job-start|job-end|log|metric|heartbeat
├─ diagnostics: doctor|network|dns|port|otlp|buffer|env|collect
├─ buffer: status|flush|retry|inspect|purge
└─ service: install|uninstall|start|stop|restart|status
```

## Domain model (MVP)
- `TelemetryEvent` (base envelope)
- `JobEvent` (`job`, `job-start`, `job-end`)
- `LogEvent`
- `MetricEvent`
- `TelemetryError`
- `BridgeConfig`
- `BufferItem`

## Components
1. **CLI Host**
   - Parse command line and invoke application services.
   - Non-strict mode default: never fail business script on telemetry failure.
2. **Config Service**
   - Create default config at `%ProgramData%/Levitec/AutomationTelemetry/config.json`.
   - Validate strongly typed options and path accessibility.
3. **Persistent Buffer**
   - Write-ahead JSON files with metadata headers.
   - Queue states: `pending`, `retrying`, `dead-letter`.
4. **OTLP Exporter**
   - Support OTLP gRPC (`4317`) and OTLP HTTP (`4318`).
   - Emit traces/logs/metrics with common resource attributes.
5. **Retry Worker**
   - Background flush loop, configurable interval and batch size.
   - Exponential backoff with jitter and max-attempt/dead-letter policy.
6. **Diagnostics**
   - DNS, TCP, TLS and OTLP probe routines.
   - Aggregated `doctor` command with actionable output.

## Reliability behavior
- Accept event → persist event → attempt export.
- On export success: mark complete and delete persisted item.
- On export failure: increment retry state and keep item durable.
- On max age/attempt threshold: move to dead-letter queue.

## Suggested milestones
1. CLI + config lifecycle.
2. Event contracts + local buffer.
3. OTLP test export + `test connection`.
4. `send job` / `job-start` / `job-end` mappings.
5. Retry worker + dead-letter.
6. Diagnostics and support bundle.
7. Windows service hosting and localhost ingest.
