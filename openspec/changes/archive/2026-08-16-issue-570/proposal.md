# Issue 570: Runner OOM / Restart Recovery for Active Agent Work

## Why

Recent production incidents (Epic 67, 2026-08-11/13) show that a Runner OOM kill or
abnormal restart silently destroys active Agent work: the kernel OOM-killed
`mohist-runner` (multi-GB peak) while several long Agent runs were active, and after
the process restarted the affected work either
surfaced as a context-free `session.abort fetch failed` failure, was terminal-failed
as `runner-lost`, or stranded in a non-dispatchable unknown after the AgentJob
report timeout. Today's closeout (`RunnerGrain.CloseoutLostAsync`) terminal-fails
ordinary workflow tasks and stage checks as `runner-lost`; AgentJobs stay silently
`Running` during the outage and then strand in the nonterminal,
non-dispatchable `Unknown` state, which reconnect never resumes; and a fast
restart strands work behind the runner's `started` recovery fence, which refuses
re-delivery forever without resolving the work. Users get an unrecoverable
failure instead of a recovery. The same incidents exposed the cascade:
one resource-runaway task OOMs the shared Runner process and takes every in-flight
work item with it, and a wedged runtime generation or hung transport blocks runtime
replacement indefinitely. Work identity and execution facts must survive Runner
process death so recovery is factual reconciliation against persisted state.

This builds on the already-landed slices of this issue: the durable work-result
journal (#545), the runtime readiness witness and per-runtime claim gating (#558),
and the Agent result-settlement arbitration (`Unknown` → `Blocked`) that already
preserves Agent-task outcomes across runner loss.

## What Changes

- Presence-loss closeout records a **recoverable interruption** — reason code,
  affected work identity, and timestamp — instead of terminal-failing ordinary
  workflow tasks and stage checks as `runner-lost`. Agent-result-settlement tasks
  keep their existing unknown/deadline arbitration.
- An AgentJob whose runner is lost projects an explicit recoverable/recovering
  state carrying the recorded interruption reason (extending the existing
  nonterminal `Unknown` semantics) instead of silently remaining `Running` during
  the outage and then stranding in a non-dispatchable `Unknown` after the report
  timeout.
- On runner reconnect, affected work re-attaches to a surviving execution or is
  re-delivered from persisted facts (ledgers, journals, runtime bindings) under the
  original work identity and idempotency boundary; a bounded deadline produces a
  definite terminal state when no runner returns; no work executes twice.
- The runner's `started` journal fence no longer strands re-delivered work after an
  abnormal restart: re-delivery reconciles against persisted facts — re-attaching
  where the facts support it, surfacing a definite outcome where they do not —
  instead of refusing silently forever.
- Late reports and observations from a previous runner process generation
  reconcile idempotently against the preserved work identity — accepted at most
  once, or acknowledged stale — never duplicate outcomes or dead ends.
- Status surfaces (Web, CLI, issue attention) render recoverable-interrupted and
  recovering states with the recorded reason instead of a bare failure.
  **BREAKING**: `runner-lost` no longer terminal-fails active workflow work;
  consumers must render the new states.
- Per-work resource containment on the runner bounds a runaway task and terminates
  it without killing the runner process or sibling in-flight work; quarantined
  OpenCode generations drain within a bounded deadline, and runtime shutdown paths
  (process-tree termination, undici dispatcher close) are bounded instead of
  waiting on hung requests.
- Deterministic tests cover the three acceptance areas: simulated OOM/abnormal
  restart entering recoverable states with reconnect recovery and no duplicate
  execution, late-report idempotency, and resource cascade protection.

An entry recorded only as `started` is a recovery fence, not permission to
execute again. Runner restart, AgentSession activity, idle state, or a missing
runtime process cannot establish a Workflow task outcome. Existing unresolved
and blocked Workflow work therefore remains subject to the explicit stop and
authoritative-result paths; this change does not release or guess-replay it.

When a restarted Runner has a durable `started` entry for an identified
Workflow Agent task, it may report only the non-terminal
`agent-result-unconfirmed` observation with the original task attempt and work
identity. The Server's durable acknowledgement of that observation permits the
Runner to retire the fence. This observation starts the existing unknown/blocked
settlement path; it never supplies a task result, infers an outcome from an
idle/runtime/artifact fact, or authorizes a replacement execution.

The same startup receipt applies to an identified AgentJob dispatch. The Server
must validate the original Runner, AgentJob, and work identities and enter the
Job's durable `Unknown` state rather than converting the observation into a
failed terminal result. This closes the restart gap for both dispatch owners;
it does not replay the AgentJob or infer a terminal result.

## Server Receipt Boundary

The Server already has one safe admission path for a recovered result: the
normal Workflow result report with the original runner, task attempt, and work
identity. A completed journal entry contains that full result and can use the
path after the Workflow has become unknown or blocked.

A `started` entry is not a terminal result receipt. It contains no result
payload, so the Server must not convert it, an AgentSession idle/completed
observation, a turn status, or a terminal task log into Workflow task success or
failure. The recovery receipt only records the non-terminal Unknown fact for
the exact original owner identity. When no completed receipt can be replayed,
the Workflow attempt remains unresolved and an AgentJob remains Unknown. A
later physical execution is not supplied by this recovery slice. The current
only Workflow abandonment control is explicit Workflow stop; if a later
product capability schedules replacement after that abandonment, it must use a
new task/work identity.

## Capabilities

- `runner-loss-work-recovery`: Control-plane and runner-fact behavior across Runner
  loss and reconnect — recoverable-interruption marking with recorded reason and
  affected work, identity-preserving re-attachment and re-delivery from persisted
  facts, bounded terminal fallback, late-report idempotency, and the
  user-visible recovering states for AgentJobs and workflow work.
- `runner-resource-isolation`: Runner-side blast-radius containment — per-work
  resource bounding and termination that does not cascade to sibling work or the
  Runner process, plus bounded runtime generation drain and transport shutdown so
  one wedged or runaway execution cannot take down the whole execution plane.

## Impact

- **Server** (`packages/server/src/Mohist.Server`): `Runner/Grains/RunnerGrain.cs`
  (presence-loss closeout), `Runner/Services/DispatchService.cs` and
  `WorkflowReportService.cs` (re-delivery and report reconciliation),
  `Workflow/Grains/WorkflowGrain.Reports.cs` plus `Workflow/Domain/Run` (closeout
  semantics and interruption representation), `Agent/Grains/AgentJobGrain.cs`
  (recovery projection, timeout path), `Agent/Services/AgentLaunchObservationAssembler.cs`.
- **Runner** (`packages/runner/src`): `runtime/host.ts` (fence/reconciliation
  interplay, poll report), `runtime/work-result-journal.ts`, `runtime/binding-recovery.ts`,
  `runtime/opencode/runtime.ts` (generation drain bound), `runtime/opencode/server-process.ts`
  (dispatcher close bound), `runtime/pi/runtime.ts`, `runtime/agent-job-executor.ts`.
- **Status surfaces**: Web workflow/agent-session views, CLI (`mo run`, agent
  observation), and issue-attention projections render the new states.
- **Deployment**: runner service resource configuration backing per-work containment.
- **Dependencies**: none new; builds on the landed result journal, readiness
  witness, result-settlement, and identity-stop protocols without redesigning them.
