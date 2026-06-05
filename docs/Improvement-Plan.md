# TelemetryBridge — Improvement Plan

_Assessment date: 2026-06-05. Reviewed commit: `19510c8`._

> ## Status — all phases implemented (2026-06-06)
> P0–P4 below were delivered across parallel branches and merged to `main`.
> Full solution builds clean; **166 tests pass (140 Core + 26 Service), 0 failures.**
>
> | Phase | Delivered |
> | --- | --- |
> | **P0** | Correctness fixes (§3.1–3.6) + xUnit test project; CI workflow + `global.json` SDK pin |
> | **P1** | Crash-safe durable buffer (temp+atomic rename), `RetryWorker` + backoff/jitter, dead-letter, `buffer status/inspect/flush/retry/purge` |
> | **P2** | `JobEvent` aligned to `Event-Contract.json` (structured `error`, `attributes`, correlation/script fields, enums); span mapping; backward-compat load; new `send job` flags |
> | **P3** | `DiagnosticsService` + `diagnostics doctor/network/dns/port/otlp/env/collect` + `test connection`; `LogEvent`/`MetricEvent` + `send log/metric/heartbeat` (best-effort OTLP) |
> | **P4** | `TelemetryBridge.Service` Worker host (singleton exporter, interval drain, localhost HTTP ingest, `/health`, heartbeat); `TelemetryBridge.PowerShell` module (`Invoke-WithTelemetry` + Pester tests) |
>
> ### Known follow-ups surfaced during implementation
> 1. **`test connection` can false-PASS against a down gRPC collector** — OTLP `ForceFlush` returns `true` asynchronously. The `doctor` TCP `port` probe reliably flags an unreachable collector; treat it as the source of truth. Consider an explicit ack/health check for `test connection`.
> 2. **`agent.listenUrl` / `enableHttpIngest` / `heartbeatIntervalSeconds` are not yet on `BridgeConfig.AgentOptions`** — the Service reads them from the raw config JSON. Promote them to the typed model for consistency/validation.
> 3. **Logs & metrics are best-effort (not buffered)** — `RetryWorker` only handles `JobEvent`. If durable logs/metrics are needed, generalize the buffer/worker over a base envelope.

## 1. Executive summary

TelemetryBridge is at an early MVP stage. The scaffolding is clean and the
architecture in `docs/MVP-Plan.md` is sound, but **the implemented code delivers
only a fraction of the planned product and the core reliability promise is not
actually met.** Today the CLI supports `config init/show/validate` and
`send job`; it writes events to a disk buffer and attempts a single OTLP trace
export. There is **no retry worker, no buffer drain, no dead-letter handling,
and no test suite** — which means the project's central value proposition
("durable buffer + retry so telemetry failures never block scripts") is
documented but not real.

The most important work, in order, is:

1. **Fix correctness bugs** that silently break configuration and leak disk.
2. **Close the buffer/retry loop** so the durability claim becomes true.
3. **Add a test project and CI** so the above can be changed safely.
4. **Reconcile the docs/contract/code drift** so users aren't misled.
5. **Then** expand the command surface and the Service/PowerShell hosts.

---

## 2. Current state (what actually exists)

| Area | Planned (`MVP-Plan.md`) | Implemented |
| --- | --- | --- |
| `config` | init, show, set, get, validate, export | init, show, validate |
| `send` | job, job-start, job-end, log, metric, heartbeat | job only |
| `test` | connection, send-trace/log/metric | none |
| `diagnostics` | doctor, network, dns, port, otlp, buffer, env, collect | none |
| `buffer` | status, flush, retry, inspect, purge | write-only enqueue |
| `service` | install/uninstall/start/stop/status | empty README only |
| Domain model | JobEvent, LogEvent, MetricEvent, TelemetryError, BufferItem | JobEvent only |
| Export | traces + logs + metrics, gRPC + HTTP | traces only (both protocols) |
| Tests | — | none |
| CI | — | none |

`src/TelemetryBridge.Service` and `src/TelemetryBridge.PowerShell` are README
placeholders and are **not part of the solution** (`TelemetryBridge.sln` only
references Core and Cli).

---

## 3. Correctness bugs (fix first — these are real, not cosmetic)

### 3.1 The buffer never drains — disk leaks forever
`BufferService.Enqueue` writes a JSON file per event but nothing ever deletes it.
The reliability spec in `MVP-Plan.md` says "on export success: mark complete and
delete persisted item," but `Program.cs` writes the file, exports, and returns —
**the file is left behind even on a successful send.** Every `send job` call
permanently grows `Data/Buffer`. Combined with the missing retry worker, the
buffer is a write-only sink.
- **Fix:** delete the buffered file on confirmed export success; keep it only on
  failure/skip. Longer term this is subsumed by the retry worker (§4).

### 3.2 The documented sample config will not bind
Config is serialized with default `System.Text.Json` options (PascalCase) and
deserialized with default options (`PropertyNameCaseInsensitive = false`).
`examples/config.sample.json` is **camelCase**, so a user who copies the sample
gets a config where most properties silently fall back to defaults (e.g.
`signoz.endpoint` → `http://localhost:4317` instead of their real endpoint).
- **Fix:** set `PropertyNameCaseInsensitive = true` (and ideally
  `JsonNamingPolicy.CamelCase` for both read and write) in `ConfigService`, and
  make the on-disk format match the documented sample.

### 3.3 Unknown config keys are silently dropped
`examples/config.sample.json` documents `service`, `signoz.useTls`,
`buffer.maxEventAgeHours`, `flushIntervalSeconds`, `batchSize`, `deadLetter*`,
`diagnostics`, `logging`, and `defaults` — **none of which exist on
`BridgeConfig`.** They are silently ignored on load. Users will reasonably
expect them to work.
- **Fix:** either implement the fields (preferred for buffer/dead-letter, see
  §4) or trim the sample to only what is honored. Don't ship a sample that lies.

### 3.4 Config location is current-directory dependent
`ConfigService.GetWorkspaceRoot()` returns `<CurrentDirectory>/TelemetryBridge`.
A CLI invoked from a PowerShell/ScriptRunner job runs in arbitrary working
directories, so each caller can get a *different* config and buffer. The plan
specifies `%ProgramData%/Levitec/AutomationTelemetry/`.
- **Fix:** default to a stable machine path (`Environment.SpecialFolder.CommonApplicationData`),
  overridable via `--config`/env var.

### 3.5 `StrictMode` is never honored
`BridgeConfig.AgentOptions.StrictMode` exists but is unused. `send job` always
returns `0`. The whole point of strict mode is to optionally fail when telemetry
fails; non-strict is the safe default but strict must be selectable.
- **Fix:** when `StrictMode` (or a `--strict` flag) is set, return non-zero on
  export failure.

### 3.6 Minor
- `ExportService.TrySendAsync` is `async` but does only synchronous work plus a
  no-op `await Task.CompletedTask`. `ForceFlush` is blocking. Either make it
  honestly synchronous or use the async flush path; today the `CancellationToken`
  timeout and the SDK timeout overlap confusingly.
- A fresh `TracerProvider` + OTLP exporter is built and torn down **per event**.
  Acceptable for a one-shot CLI, but wasteful and must be hoisted to a singleton
  once the Service host or batched sends exist.
- Verbose handling is fine, but there is no real structured logging for the
  bridge's own operations (failures currently only hit `Console.Error`).

---

## 4. Close the reliability loop (the core feature)

This is the project's reason to exist and is currently missing.

1. **Buffer read/dequeue API** — `BufferService` needs `List`, `Read`, `Delete`,
   and atomic state transitions (`pending` → `retrying` → `dead-letter`). Use a
   write-temp-then-rename pattern so a crash mid-write can't corrupt the queue.
2. **Retry worker** — background flush loop with configurable interval and batch
   size, exponential backoff + jitter, and a max-attempt / max-age threshold that
   moves items to a dead-letter folder.
3. **`buffer` commands** — `status`, `flush`, `retry`, `inspect`, `purge` so the
   queue is operable in production.
4. **Wire `Program.cs` send path to the loop** — enqueue, try once, leave durable
   on failure; the worker handles the rest. Delete on success (fixes §3.1).

This makes the architecture diagram in the README true for the first time.

---

## 5. Testing & CI (do alongside §3–4, not after)

- **Add `tests/TelemetryBridge.Core.Tests`** (xUnit). Priorities:
  - Config round-trip (init → load), case-insensitive binding, validation errors.
  - Buffer enqueue/dequeue/delete, dead-letter transition, crash-safety of writes.
  - Export protocol/endpoint resolution (`grpc`, `http`, `http/protobuf`, the
    `/v1/traces` path append) — unit-testable without a live collector.
  - Retry/backoff policy timing math.
  - Export integration against an in-process OTLP collector or a stub HTTP
    listener (assert payload + headers).
- **Add `.github/workflows/ci.yml`**: `dotnet restore/build/test` on push + PR,
  matrix on the pinned .NET 10 SDK. The repo already has the `dotnet` 10.0.204
  SDK locally, so this is low-friction.
- Add a `global.json` pinning the SDK so CI and dev stay in sync.

---

## 6. Contract & model alignment

- `JobEvent` does not match `docs/Event-Contract.json`: missing `correlationId`,
  `parentCorrelationId`, `instance`, `scriptPath/Name/Version`, `exitCode`, the
  structured `error` object, and free-form `attributes`. `EventType` is hardcoded
  to `"job"` so `job-start`/`job-end` can't be expressed.
- `status` values differ: code defaults to `"succeeded"`, contract enum includes
  `started/succeeded/failed/warning/skipped/cancelled/timeout/interrupted/unknown`.
- **Fix:** make `JobEvent` the contract's shape (or generate the model from the
  schema), validate incoming status against the enum, and map `attributes` onto
  span tags in `ExportService`.

---

## 7. Feature expansion (after the foundation is solid)

- **Diagnostics** (`doctor`, `network`, `dns`, `port`, `otlp`, `env`, `collect`)
  — high operator value for an on-prem tool; `doctor` first.
- **`test connection` / `send-trace`** — quick win, reuses `ExportService`.
- **Logs & metrics export** — extend beyond traces per the plan.
- **Windows Service host** (`TelemetryBridge.Service`) — owns the retry worker and
  the singleton exporter; add localhost HTTP ingest (`listenUrl` in the sample).
- **PowerShell module** (`Invoke-WithTelemetry`) — the primary ergonomic entry
  point for the target users; thin wrapper over the CLI.
- Replace hand-rolled arg parsing with `System.CommandLine` once the surface
  grows past a couple of verbs.

---

## 8. Suggested sequencing

| Phase | Work | Outcome |
| --- | --- | --- |
| **P0** | §3 correctness bugs + §5 test project & CI | Existing features actually work and are protected by tests |
| **P1** | §4 buffer/retry/dead-letter loop | The durability promise becomes real |
| **P2** | §6 contract alignment + §3.4 stable config path | Data is correct and portable |
| **P3** | §7 diagnostics, logs/metrics, test commands | Operable, full telemetry coverage |
| **P4** | §7 Service host + PowerShell module | Production deployment shape |

Each phase is independently shippable. P0 and P1 together are the difference
between "a buffer-shaped folder" and "a telemetry bridge."

---

## 9. Quick wins (under ~1 hour each)
- Delete buffered file on export success (§3.1).
- `PropertyNameCaseInsensitive = true` + camelCase policy (§3.2).
- Add `global.json` + a minimal CI workflow (§5).
- Honor `StrictMode` / add `--strict` (§3.5).
- Trim or document the unsupported sample-config keys (§3.3).
- Add `LICENSE`/`README` build badge once CI exists.
