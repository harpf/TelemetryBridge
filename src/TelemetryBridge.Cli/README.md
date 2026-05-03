# TelemetryBridge.Cli

Planned .NET 10 command host for `levitec-telemetry`.

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
