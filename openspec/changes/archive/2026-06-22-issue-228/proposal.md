## Why

The server's own execution is a black box: an HTTP request enters, dispatches via SignalR, triggers Orleans grains, queries the DB through EF Core, and fires outbound HttpClient calls — but none of this shows up as trace data. #219 shipped a collector that can receive traces, but only external apps feed it; the server contributes nothing, so local debugging and programmatic trace-based diagnosis cannot see Mohist's internal execution chain. Instrumenting the server to emit its own traces closes that loop and makes the full execution path observable end-to-end.

## What Changes

- Enable the OpenTelemetry SDK in the server host startup, exporting traces via OTLP HTTP to a configurable endpoint (default `http://localhost:4318/otel`, i.e. #219's ingest).
- Add five sources of automatic instrumentation so a single request produces one unbroken trace across all segments:
  1. **ASP.NET Core inbound HTTP** — one span per request, with route template, status code, duration.
  2. **SignalR hub method invocation** — one span per hub method (runner dispatch / web events) as a child of the calling context (built-in `Microsoft.AspNetCore.SignalR.Server` source on .NET 10).
  3. **Orleans grain calls / persistence / reminders** — Orleans 10 native `ActivitySource`.
  4. **EF Core database queries** — one span per SQL statement, with SQL text and duration.
  5. **Outbound HttpClient calls** (self-update check, readiness probe) — one span per outbound call.
- **Exclude `/otel/` from inbound HTTP instrumentation.** The #219 collector runs in the same process under the `/otel/` prefix on port 4318; if the server's own trace POSTs to it were themselves traced, the trace-send request would emit a trace that triggers another send — an infinite self-feedback loop of pure noise.
- Exporter failure (endpoint unreachable) MUST be non-fatal: no thrown exceptions, no request blocking. Tracing degrades silently.
- Add a master on/off switch and per-deployment endpoint configuration through the existing config system (`~/.mohist/config.jsonc`) with `MOHIST__*` environment-variable override. When disabled, no span is produced or sent, and server behavior is unchanged.
- Use only community / built-in automatic instrumentation backed by standard `ActivitySource`. No custom business spans, no sampling, no log/metric emission (trace-only).

## Capabilities

### New Capabilities

- `server-otel-tracing`: The server emits its own OpenTelemetry traces for the full inbound→SignalR→Orleans→EF→outbound-HttpClient execution chain via automatic instrumentation, exports them over OTLP HTTP to a configurable endpoint (default the local #219 collector), excludes its own `/otel/` ingest path to avoid self-feedback, degrades silently on exporter failure, and can be fully disabled via a master switch.

### Modified Capabilities

<!-- No existing spec-level requirements change. Tracing is additive to host startup and does not alter the HTTP API contract, the server-daemon lifecycle, or the Orleans/SignalR behavior surfaced by existing specs. -->

## Impact

- **Host startup / DI pipeline**: `Program.cs` and `MohistServiceRegistration` (`AddMohistServerCore`) gain OpenTelemetry SDK registration with OTLP HTTP exporter and the five instrumentation sources; Orleans silo config (`MohistSiloRegistration`) carries the Orleans source.
- **New NuGet dependencies**: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, and the instrumentation packages (`AspNetCore`, `HttpClient`, `EntityFrameworkCore`; SignalR and Orleans ship built-in `ActivitySource` on .NET 10 / Orleans 10).
- **Configuration**: New `Mohist:Otel` section (endpoint, enabled flag) loaded by the existing `AddMohistConfigFile` path, overridable via `MOHIST__*` env vars — mirrors the pattern of `AgentJobOptions` / `AttachmentStorageOptions`.
- **Interaction with #219**: The `/otel/` inbound-path filter is a correctness constraint against the same-process collector (#219 binds port 4318 under `/otel/`); without it, trace emission feeds itself.
- **Non-impact**: No HTTP API contract changes, no database schema changes, no SignalR/Orleans message changes, no breaking changes. Behavior is zero-regression when the master switch is off.
