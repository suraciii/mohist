# Self-Review — Issue 484

Reviewed artifacts: `proposal.md`, `design.md`, `tasks.json`, and all three spec files,
against the issue body (acceptance criteria, non-goals, domain context) and the current
codebase.

## Coverage check (acceptance criteria → specs)

All 11 acceptance criteria map to at least one spec requirement with scenarios:

| AC | Spec coverage |
|---|---|
| AC1 execution → idle | agent-session-activity §"An execution outcome returns the session to idle" |
| AC2 no terminal state; consumers use activity | agent-session-activity §"Consumers read current activity…" |
| AC3 flat transcript, no exec sub-resources | session-transcript §"The transcript is a flat append-only session record" |
| AC4 normal ops reuse binding | **gap — see F3** |
| AC5 confirmed-missing recovery, one session, once | runtime-binding-recovery §"Confirmed-missing recovery…" |
| AC6 non-recovery conditions | runtime-binding-recovery §"Non-recovery conditions…" |
| AC7 CAS expected-binding, no submit if unconfirmed | runtime-binding-recovery §"Binding replacement is compare-and-swap…" + §"A created-but-unbound candidate…" |
| AC8 binding preserves identity; context_reset no session IDs | runtime-binding-recovery §"The session stores exactly one current binding" + session-transcript §"Binding replacement is recorded as a context reset fact" |
| AC9 late events rejected | runtime-binding-recovery §"Binding replacement is compare-and-swap…" (late-event scenario) |
| AC10 uniform across runtime/source | agent-session-activity §"Activity semantics are uniform…" |
| AC11 work results independent | agent-session-activity §"Work results are independent of session activity" |

## Findings

### F1 — Significant: OpenCode Reset is required by spec but no task implements it

The spec normatively requires Reset to work unconditionally:

> `runtime-binding-recovery/spec.md` — "A user Reset… SHALL replace the binding using the
> same idle-only compare-and-swap path." Scenario: "A reset is requested while idle → a new
> empty Runtime Session SHALL be created and bound via compare-and-swap."

AC10 requires OpenCode and Pi to follow "相同的… Reset 语义." But OpenCode Reset is not
implemented today: `command-runtime.ts:169-171` returns `{ ok: false, error: "unavailable" }`
for any OpenCode `SessionCommand`, while Pi Reset works (`dispatchPiReset` → `PiRuntime.reset`).

The design acknowledges this as unresolved (Open Question 2: "Decide whether #484 covers both
[OpenCode Reset] or only recovery"), but:

- No task implements the runner-side OpenCode Reset command path.
- T-002 unifies the **server-side** rebind (so the server is ready), but the runner-side
  `callSessionCommand` still hard-codes `unavailable` for OpenCode.
- T-003 implements OpenCode **recovery** (create-on-missing via `resolveOrRecoverBinding`),
  which is a different code path from the explicit `POST /reset` → `SessionCommand` command.

After all four tasks, an OpenCode session that is `idle` and receives a Reset still fails with
`unavailable`, violating the spec scenario and AC10.

**Fix needed:** Either (a) add a task/acceptance criterion that wires the OpenCode
`SessionCommand` reset through `OpenCodeRuntime.createSession` + the unified rebind, or
(b) explicitly scope the spec to document OpenCode Reset as a pre-existing gap excluded from
this change and adjust the scenario wording. The current state is a three-way inconsistency
between spec (normative SHALL), design (open question), and tasks (no coverage).

### F2 — Moderate: ContextExhaustionClassifier loses its only trigger

`ContextExhaustionClassifier` runs "on every `session.closed` runtime event"
(`ContextExhaustionClassifier.cs:15`) to annotate `failureCategory` and emit
`AgentSessionContextExhausted` domain events (consumed by the Web for context-exhaustion
error rendering and retry blocking). The grain calls it via `ClassifySessionClosedPayload`
(`AgentSessionGrain.cs:1153`) and `EmitContextExhaustionDomain` (`:1202`).

T-001 removes `session.closed` writing and the grain's `ClassifySessionClosedPayload` /
session.closed special-casing. After T-001 the classifier has no trigger: it becomes dead
code, and context-exhaustion enrichment silently stops. No task addresses whether the
classifier should be removed, re-wired to the turn-failed / runtime-diagnostics path, or
explicitly left as inert dead code.

**Fix needed:** T-001 (or a follow-up note) should state the intended fate of
`ContextExhaustionClassifier` and the `AgentSessionContextExhausted` event so the build agent
does not leave behind silently-dead classification or an unhandled regression in
context-exhaustion surfacing.

### F3 — Minor: No positive "reuse binding" scenario for normal operations (AC4)

AC4: "正常 task、retry、Follow-up、Compact、模型变化和 Runner 重启继续复用 current
binding，不创建新的物理 Session." The spec covers this only negatively —
runtime-binding-recovery §"Non-recovery conditions" says recovery does not trigger when the
session is not missing. There is no scenario that positively asserts a healthy binding is
reused (not resolved, not replaced) across model change, retry, Compact, or Runner restart.

A build agent implementing `resolveOrRecoverBinding` (T-003) benefits from an explicit
scenario confirming that the resolve step returns `ready` and the binding is untouched for
these normal operations, so it does not accidentally probe or replace a healthy binding.

**Fix needed:** Add a positive scenario (e.g. to runtime-binding-recovery or
agent-session-activity) such as: "WHEN a retry or model change occurs and the current Runtime
Session is healthy, THEN the binding is reused without resolve-probe or replacement."

### F4 — Minor: Design internal inconsistency on the `unknown` watchdog

Design D2 describes the runner-disconnect → `unknown` server watchdog as part of the activity
model ("the server sets it on runner disconnect mid-turn via RunnerConnectionTracker").
Design Open Questions separately says its scope is undecided ("Whether the runner-disconnect →
unknown server watchdog is in this change or a follow-up"). T-001 includes it in its
description, resolving the ambiguity for the task layer, but the design document contradicts
itself.

**Fix needed:** Remove the open question or mark D2's watchdog sentence as tentative so the
design is internally consistent.

## Task graph check

- `tasks.json` is valid JSON; 4 tasks form a valid DAG (T-001 → T-002 → T-003 → T-004).
- Every `dependsOn` references a strictly-lower-priority task.
- Every task has acceptance criteria with test verification; `passes` is `false` on all.
- `spec` anchors resolve to real requirements in the spec files.
- No standalone test/move/rename tasks.

## Verdict

F1 is a three-way inconsistency (spec requires OpenCode Reset unconditionally; design leaves it
as an open question; no task implements it). F2 leaves a classifier trigger removed without
addressing the consequence. These must be resolved before the plan is ready to build.

<promise>FAIL</promise>
