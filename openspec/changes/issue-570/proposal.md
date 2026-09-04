## Why

Two production incidents (2026-08-11/13, Epic 67) show that a Runner OOM kill or abnormal restart silently destroys active Agent work: the kernel OOM-killed `mohist-runner` (peak 9.7–20.2 GB) while multiple long Agent runs were active, and after systemd's restart the affected work either surfaced as a context-free `session.abort fetch failed` failure or was terminal-failed as `runner-lost`. Adjacent wedges amplify the blast radius: a quarantined OpenCode generation waits for `generation.drained` with no deadline, the undici dispatcher close waits on hung requests, and the RunnerHost readiness gate stops polling entirely while a runtime is unhealthy — so one wedged or resource-runaway task converts into whole-Runner loss and the failure of every in-flight task. Work identity and execution facts must survive Runner process death so recovery is factual reconciliation against persisted state, not retry roulette.

## What Changes

- Control-plane closeout on Runner presence loss records a recoverable interruption (reason code plus affected work) instead of terminal-failing active workflow work as `runner-lost`; retained AgentJob ledgers project an explicit recovery state.
- On Runner reconnect, affected work re-attaches or re-delivers from persisted facts under the original work identity and idempotency boundary; a bounded deadline produces a clear terminal state when no Runner returns, and no work executes twice.
- Late reports and observations from a previous Runner process generation reconcile idempotently against the preserved work identity — accepted at most once or acknowledged stale — never 404 dead ends or duplicate outcomes.
- Runner control-plane liveness (poll/report/heartbeat) is decoupled from runtime (OpenCode/Pi) readiness: an unavailable runtime defers only its own runtime-bound work with preserved identity and report key while ordinary work continues claiming; deferred work is never reported as a synthesized failure, and an outcome that is unknown is never replayed.
- A quarantined OpenCode generation drains within a bounded, injectable deadline; runtime shutdown paths (process-tree termination, undici dispatcher close) are bounded and destroy hung transports instead of blocking a replacement generation.
- Resource runaway is contained per work item so one long task cannot OOM the shared Runner process and cascade-kill other in-flight work on the same Runner.
- **BREAKING**: `runner-lost` no longer terminal-fails active workflow work; Web, CLI, and issue-attention consumers must render the new recoverable-interrupted / recovering states with their recorded reasons instead of a failure.

## Capabilities

- `runner-runtime-liveness`: Runner control-plane poll/report continuity independent of runtime health, runtime-specific work deferral with preserved identity, and bounded recovery for wedged runtime generations, process-tree termination, and transport shutdown.
- `runner-loss-work-recovery`: Control-plane behavior across Runner loss and reconnect — recoverable interruption marking with recorded reason, identity-preserving redelivery and reconciliation, bounded terminal fallback, late-report idempotency, and user-visible recovery status for AgentJobs and workflow work.
- `runner-resource-isolation`: Per-work resource bounding on a Runner so a runaway task is contained and terminable without cascading to other work or the Runner process.

## Impact

- **Runner** (`packages/runner/src`): `runtime/host.ts` (readiness gate, poll loop, reported set), `runtime/opencode/runtime.ts` (generation drain and quarantine), `runtime/opencode/server-process.ts` (dispatcher close), `runtime/pi/runtime.ts`, `runtime/agent-job-executor.ts`, `server/connection.ts`, `system/process.ts` (terminateTree bounding).
- **Server** (`packages/server/src/Mohist.Server`): `Runner/Grains/RunnerGrain.cs` (presence-loss closeout), `Runner/Services/DispatchService.cs` and `WorkflowReportService.cs` (redelivery and report reconciliation), `Workflow/Grains/WorkflowGrain.Reports.cs` (closeout semantics), `Agent/Grains/AgentJobGrain.cs` (timeout, Unknown, redelivery).
- **Status surfaces**: Web workflow/run views, CLI (`mo run`, `mo runner`), and issue attention/inbox projections must expose recoverable-interrupted and recovering states with actionable reasons rather than `runner-lost` / `session.abort` failures.
- **Deployment**: runner service resource configuration (systemd/cgroup) backing per-work isolation.
- **Dependencies**: builds on #589 `workflow-agent-result-settlement` (unknown Agent-result settlement) and #562 identity-based stop; does not redesign those protocols.
