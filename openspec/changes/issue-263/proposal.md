## Why

A single developer who splits a feature into linked issues under an Epic and orders their dependencies currently must manually `mo issue start` the next issue after each completion. Every hand-off stalls throughput — especially across async/overnight runs — directly contradicting Mohist's promise to scale software output. The Epic should become a self-driving entity: once started, it advances linked issues one-by-one to completion or until the user pauses.

## What Changes

- **BREAKING**: Epic `active` state is renamed to `idle` ("exists, not yet started"); existing `active` data is migrated to `idle`.
- New epic lifecycle command `mo epic start {n}`: transitions `idle` → `running` and immediately starts the first startable linked issue (or becomes a running-but-idle, observable epic if none is startable).
- New epic lifecycle command `mo epic pause {n}`: transitions `running` → `paused`; stops starting the next issue but does **not** interrupt the in-progress one.
- New epic lifecycle command `mo epic resume {n}`: transitions `paused` → `running` and re-evaluates/advances.
- Autonomous progression: when a running epic's linked issue reaches a **terminal** state — `done` (delivered) **or** `cancelled` (removed from scope) — the epic re-evaluates and starts the next startable issue, or auto-closes to `done` when all are delivered (reuses #177 readiness).
- Serial invariant: at most one in-progress linked issue at a time (runner capacity N=1), enforced as an execution-plane policy rather than an aggregate invariant (leaves room for future multi-runner parallelism).
- Execution failure handling: a failed issue stays in `in_progress` (health blocked); the serial invariant naturally holds the epic, visible on the dashboard — no new epic-level failure state.
- Dashboard `nextIssueReason` exposes why a running-but-idle epic has no startable issue (next is draft, externally blocked, etc.).
- Start/Pause/Resume are idempotent (no-op when epic is already in the target state).
- Concurrency safety: progression decisions are owned by the EpicGrain; Orleans turn-based concurrency guarantees a `Pause` is applied before any in-flight terminal-event-triggered auto-advance (no races).
- Web UI: Epic detail-page header surfaces lifecycle actions by state — `idle` shows **Start Epic**, `running` shows **Pause**, `paused` shows **Resume**, `done`/`closed` shows none.
- Web UI: the one-shot **Start** button on the "Next Issue" card (`epic-detail-next-start`) is **removed**; that card becomes information-only (next issue + reason). Per-issue-row inline Start (`linked-issue-start`) is **retained** for the manual single-issue journey.
- Workflow core code remains unaware of issue/epic (dependency direction preserved).

## Capabilities

### New Capabilities
<!-- None: this change extends existing capabilities rather than introducing brand-new ones. -->

### Modified Capabilities
- `epic-lifecycle`: Adds the autonomous-progression state machine — `active`→`idle` rename, new `running` state, and `start`/`pause`/`resume` lifecycle transitions; adds terminal-event-triggered auto-advancement (on both `done` and `cancelled`) beyond the existing auto-done-on-completion; adds serial single-in-progress advancement, cancel-skipping, idempotent transitions, and running-but-idle observability.

## Impact

- **Server / EpicGrain (Orleans)**: State machine extended (`active`→`idle`, new `running`); new `Start`/`Pause`/`Resume` commands; progression logic (`ReconcileAfterTerminalAsync` / `TryStartNext`) owned by the grain; `SelectStartableNext` factored out of `EpicProgress` read-model for shared ordering + cancel-skip semantics.
- **Server / event wiring**: `EpicAutoDoneHandler` generalized to listen on both terminal events (`IssueWorkCompleted` and `IssueClosed`) and dispatch a unified reconcile call.
- **Server / schema**: Migration of existing `active` epic rows to `idle`; new lifecycle enum value(s).
- **CLI** (`mo epic start|pause|resume`): new commands against the epic lifecycle API.
- **HTTP API**: New endpoints for start/pause/resume epic lifecycle actions.
- **Web UI**: Epic detail-page header lifecycle action buttons (state-driven); removal of `epic-detail-next-start`; keep `linked-issue-start`.
- **Workflow core**: No new dependency on issue/epic (invariant preserved).
- **Dependencies**: None new.
