## Why

A Workflow agent turn that hits its execution deadline is killed mid-flight: changes uncommitted, no progress record, retry restarts from a clean but information-less state. The agent never gets a chance to wrap up. With `OpenCodeRuntime` now landed (#409) and OpenCode's mid-turn steering verified (injected `promptAsync` messages are picked up at iteration boundaries, same path as user follow-ups), the runtime can give the agent a 5-minute warning before the deadline so it commits current work and leaves a record — making deadline failures resumable instead of destructive.

## What Changes

- Add a per-turn two-stage wrap-up protocol to `OpenCodeRuntime`: when a deadline is declared for a Prompt, the runtime injects a single task-agnostic warning via `client.session.promptAsync()` 5 minutes before the deadline (fire-and-forget), then on deadline calls `client.session.abort()` and returns `interrupted` (current behaviour).
- When the declared deadline is less than 5 minutes away, inject the warning at turn start instead of skipping it. Each Prompt execution warns at most once.
- Surface the deadline declaration on the turn request so the runtime owns the schedule (today the runtime only sees an opaque `AbortSignal` and cannot know when to warn). Default deadline is 60 minutes when the executor does not specify one; explicit overrides win.
- A turn that ends normally after being warned is **not** aborted; its result is evaluated by the existing task completion contract. The runtime's job ends at "warn once + abort on deadline"; commit/record semantics stay in each task's own prompt.
- Warning text is task-agnostic and fixed by the runtime: stop new work → commit current changes → leave a record in this task's progress channel → end. It does not name markers, files, or specific contracts.
- The injected warning flows through the same session message stream as user follow-ups; its appearance in the transcript is produced by the existing event projection (UI-visible with no extra plumbing).
- All scheduling reads an injectable clock; no `setTimeout`-driven wall-clock dependency in tests.

**Non-Goals** (per issue): no deadline value exposed to prompts/variables; no auto-commit/rollback of residual state on termination; no change to the 60-minute default or auto-resume; no silent/quiet detection; no separate AgentJob handling (it picks this up naturally once #410 routes it through the same runtime).

## Capabilities

- `opencode-turn-deadline`: The two-stage deadline wrap-up protocol inside `OpenCodeRuntime` — per-turn deadline declaration on the turn request (default 60 min, executor-overridable), single fire-and-forget warning injection via `promptAsync()` 5 minutes before deadline (or at turn start when shorter), unchanged abort-on-deadline behaviour, no-abort when the warned turn ends on its own, task-agnostic warning text, transcript visibility through existing event projection, and full fake-clock drivability.

## Impact

- **Runner runtime** (`packages/runner/src/runtime/opencode/`): `turn.ts` gains the warning scheduler and the "warned-once / ended-normally" guard around the existing abort path; `types.ts` extends `RuntimeTurnRequest` with the deadline declaration; `errors.ts` `interrupted` diagnostic already covers deadline abort. The clock is injected through the runtime deps so tests drive it deterministically.
- **Runner Actions** (`packages/runner/src/actions/opencode.ts`): the Action declares the deadline (default 60 min, honouring any executor-level override) when building the turn request, then hands both request and the existing abort signal to `runTurn`.
- **Testing**: extend the OpenCode runtime fakes to assert (a) warning injected exactly once at 5-minutes-to-deadline, (b) warning injected at turn start when deadline < 5 min, (c) no warning when no deadline declared, (d) abort + `interrupted` on deadline, (e) no abort when the warned turn completes normally, (f) warning text carries no marker/filename. All cases driven by a fake clock; no real timers. Smoke record against real OpenCode confirming a mid-turn `promptAsync` is picked up by the running turn (per issue acceptance criteria).
- **Compatibility**: persisted inputs and config are unchanged — the deadline is declared at runtime by the Action/executor, not stored on persisted state. AgentJob execution picks up the protocol once #410 routes it through `OpenCodeRuntime`; no parallel mechanism is introduced here.
