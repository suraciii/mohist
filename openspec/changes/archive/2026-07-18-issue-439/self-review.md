# Self-Review — issue-439

Reviewer role: plan reviewer (no fixes applied). Reviewed `proposal.md`,
`design.md`, `specs/opencode-turn-deadline/spec.md`, `tasks.json` against the
issue body and the current codebase.

## Verification of factual claims

Spot-checked the design's codebase claims; they hold:

- `actions/opencode.ts:123` does pass `context.signal` straight through to
  `runtime.runTurn` (no per-turn deadline layered).
- `DEFAULT_TIMEOUT_MS = 60 * 60 * 1000` exists only in
  `actions/acp/session-strategies.ts:39` (ACP path, slated for #410 removal).
  No equivalent on the OpenCode path today — so the design's "no deadline is
  declared on the Workflow OpenCode path today" is accurate.
- `createTimeoutSignal(parent, timeoutMs)` in `system/timeout-signal.ts` does
  what D3 needs; `dispose()` is idempotent and safe in `finally`.
- `numberInput(context.with, "timeout")` returns `undefined` for missing /
  non-finite values, so `?? DEFAULT_TURN_DEADLINE_MS` composes correctly.
- Tasks.json is valid JSON; the dependency graph is a DAG (T-002 → T-001);
  priority ordering is consistent; every spec anchor referenced from
  `tasks.json` matches an actual `### Requirement:` heading (slug-for-slug,
  including the typo in requirement 4 — see finding F-3).

## Findings

### F-1 — BLOCKING: the transcript-visibility requirement assumes projection code that does not exist

Spec requirement "The warning is visible in the transcript via the existing
follow-up path" states:

> The warning's appearance in the transcript SHALL be produced by the existing
> event projection with no special-case plumbing.

Design D4 / Migration Plan repeat this assumption ("the warning enters via the
same path as a user follow-up, so it benefits from existing event projection
and UI plumbing"). The issue's acceptance criterion
`警告消息出现在该 session 的 transcript 中（经事件投影，UI 可见）` depends on
the same premise.

That premise is false on the current `master`:

- `packages/runner/src/runtime/opencode/event-subscription.ts:6` explicitly
  punts transcript projection to "higher-level code (T-004 turns / T-005
  session commands)".
- `runtime/opencode/turn.ts` consumes events only for the retry tracker; it
  emits zero `runtimeEvents` to the server.
- `actions/opencode.ts` emits zero `runtimeEvents`.
- The #409 commit (`0c203399f`) removed the Workflow-path usage of
  `actions/acp/session-events.ts`, which was the ACP-era projection path
  that called `AppendRuntimeEventsAsync` via `runtimeEvents` payloads.
- The only remaining emitters of `runtimeEvents` are the ACP session-events
  path (AgentJob only, going away in #410), `followup-handler.ts`, and
  `host.ts:298` — none of which are on the OpenCode Workflow turn path.

So for an OpenCode Workflow session today, the runner produces no transcript
envelopes at all. The Web fetches transcript data from the Mohist server
endpoint, which only has what the runner (or another caller) appended. There
is nothing for the injected warning to "flow through".

T-001 as scoped adds the warning injection (the `promptAsync` call) but no
projection. With T-001 alone, the warning lands in the OpenCode session
message stream and is processed by the running turn — but it does NOT appear
in the Mohist transcript, because no code path projects OpenCode session
events to `runtimeEvents`/`TranscriptEnvelope`. The acceptance criterion
cannot be satisfied as scoped.

This needs an explicit decision before build: either (a) expand T-001 to add
the minimum projection needed (the warning's user-follow-up message →
`runtimeEvents` append, at least), or (b) soften the spec's
"existing-event-projection" language to "the warning is injected into the
OpenCode session message stream" and explicitly defer UI-side transcript
visibility to #410 (which is already bringing the projection layer over).

### F-2 — Spec/design contradiction on the clock injection mechanism

Spec requirement "Deadline scheduling is driven by an injectable clock"
states:

> All time-based scheduling … SHALL read from a clock injected through the
> runtime's dependencies.
>
> **THEN** the clock used for warning scheduling and deadline detection
> SHALL be the injected fake clock

Design D6 and T-001 notes prescribe the opposite:

> **Alternative considered:** a hand-rolled `Clock`/`Scheduler` injected
> through `TurnExecutionDeps`. Rejected as over-engineering — the existing
> sanctioned TS pattern per `AGENTS.md` is `vi.useFakeTimers()`.
>
> [T-001 notes] Do NOT introduce a hand-rolled Clock abstraction;
> `vi.useFakeTimers()` is the sanctioned TS pattern per AGENTS.md.

`vi.useFakeTimers()` is a global vitest mechanism; it is not "a clock
injected through the runtime's dependencies". The spec commits to one design
(di-injected clock); the design + tasks commit to another (global fake
timers). At implementation time the implementer cannot satisfy both. Pick
one and align the other:

- Either fix the spec scenario to say "tests drive the protocol via
  `vi.useFakeTimers()`; advancing the fake clock via
  `vi.advanceTimersByTimeAsync(…)` triggers the warning injection and the
  deadline abort path" (matches design + AGENTS.md).
- Or fix the design to add a small `Clock`/`Scheduler` seam on
  `TurnExecutionDeps` and have the production path pass a real clock.

### F-3 — Typo in spec requirement heading propagates to tasks.json anchor

Spec requirement 4 heading reads:

> ### Requirement: The deadline terminates **an still-running** turn as
> interrupted

Should be "a still-running". `tasks.json` references the slugified anchor
`#the-deadline-terminates-an-still-running-turn-as-interrupted` (which does
match the current heading, so the link resolves), but if either side is
fixed in isolation the link breaks. Fix both together.

### F-4 — Spec does not state the "turn ends before warning fires" case

Spec requirement 2 says the runtime "SHALL inject exactly one wrap-up
warning". Design D4 specifies that the warning timer is cleared in the same
`finally` that disposes the timeout handle, so if the prompt response
arrives before the warning is due, zero warnings are injected. This is the
correct behaviour, but the spec does not state it. "Exactly one" is easy to
misread as "always exactly one regardless of when the turn ends". Add a
scenario like:

- **WHEN** the awaited prompt response arrives before the warning is
  scheduled to fire
- **THEN** the runtime SHALL NOT inject the warning
- **AND** SHALL NOT call `client.session.abort()`

### F-5 — "executor override" terminology is loose

Proposal, spec scenario "An executor override wins over the default", and
design D2 all use "executor override", but D2 actually resolves the override
from `numberInput(context.with, "timeout")` — which is task-level input
(`with:`), not the WorkExecutor. The Mohist `WorkExecutor` is a specific
class and does not declare a per-turn deadline. The intent is clear, but
the wording could mislead. Suggest s/executor override/task-level `with.timeout`
override/ in the spec scenario.

### F-6 — T-002 `mode: AFK` may be optimistic

T-002 captures a real-OpenCode smoke. The acceptance criteria honestly handle
the no-OpenCode case ("records that fact explicitly … does not fabricate
evidence"), but `mode: AFK` implies the agent can complete the task
autonomously. An AFK agent cannot install a real OpenCode CLI if it is
absent. Either change the mode to `HITL` (so a human can provide the
environment), or make the notes explicit that an AFK run is permitted to
produce a "gap recorded" outcome and that this is acceptable for the issue's
acceptance criterion.

## What looks right

- Capability boundary (`opencode-turn-deadline`) is the right single slice;
  the tasks correctly merge interface + implementation + call-site + tests
  into T-001 rather than over-granularising.
- Smoke as a separate task (T-002) is the right call: different environment
  requirement, produces an artifact rather than code.
- Design D1, D3, D5, D7 are sound; the alternatives-considered sections are
  genuine.
- Risk register is honest about the promptAsync ordering race, the
  long-tool-call pickup delay, and the "deadline introduced for the first
  time" behaviour change.
- Warning text (D5) satisfies the issue's "task-agnostic, no markers, no
  filenames" requirement; the regex-blacklist test called for in T-001's
  acceptance criteria is a good guard against drift.
- Migration / rollback plan is accurate: additive at the boundary, single
  revert, no persisted-state migration.

## Verdict

F-1 is a blocking scope/feasibility problem: the plan asserts an "existing
event projection" that does not exist on the OpenCode Workflow path, and a
stated issue acceptance criterion cannot be met with T-001 as scoped. F-2 is
a real spec-vs-design contradiction that will trip implementation. Both need
to be resolved before build. F-3 through F-6 are smaller fixes that should
ride along.

<promise>FAIL</promise>
