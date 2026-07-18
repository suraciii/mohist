## Context

`OpenCodeRuntime` landed in #409 runs Workflow Inline Agent turns over the native `@opencode-ai/sdk/v2` surface. Its turn layer (`packages/runner/src/runtime/opencode/turn.ts`) awaits `client.session.prompt()` for completion and treats the caller's `AbortSignal` as the sole deadline backstop: on abort it calls `client.session.abort()` and returns `interrupted`.

Two gaps are present today:

1. **No deadline is declared on the Workflow OpenCode path.** `actions/opencode.ts:123` passes `context.signal` straight through; that signal carries only work-level cancellation (runner shutdown, user cancel). The 60-minute default from `design/runtimes/opencode.md` exists only in `actions/acp/session-strategies.ts` (`DEFAULT_TIMEOUT_MS`), which is ACP-only and is removed by #410. So a hung OpenCode Workflow turn today runs unbounded.
2. **No wrap-up warning.** When the deadline (once wired) fires, the agent is killed mid-flight with no chance to commit or record.

OpenCode's mid-turn steering is already verified (issue body, plus #409 smoke): a `client.session.promptAsync()` message injected while a turn is running enters the Session message stream and is picked up at the next iteration boundary — same path as a user follow-up. Long tool calls delay pickup; worst case degrades to a direct abort, which is accepted.

This change owns both pieces in one place: the runtime's turn layer. Per the issue, the design is fixed — implementation follows `design/runtimes/opencode.md`「回合期限与两段式收尾」, no redesign.

Stakeholders: Workflow Inline Agent path (this issue); AgentJob path picks the protocol up naturally once #410 routes it through the same `OpenCodeRuntime` (no parallel mechanism here).

## Goals / Non-Goals

**Goals:**

- Introduce a per-turn deadline declaration on `RuntimeTurnRequest` and have the `mohist/opencode` Action declare it (default 60 min, `with.timeout` override in ms).
- Inject exactly one task-agnostic wrap-up warning via `client.session.promptAsync()` 5 minutes before the deadline (or at turn start when the deadline is shorter than 5 minutes).
- Preserve the existing deadline-abort behaviour unchanged: deadline reached while the prompt is still in flight ⇒ `client.session.abort()` + `interrupted`.
- Never abort a turn that has already completed, whether or not the warning fired.
- Keep the warning text task-agnostic and runtime-owned.
- Make every deadline-related test fake-clock driven.

**Non-Goals** (per issue):

- Do not surface the deadline value to the prompt body, system variant, or any agent-visible variable.
- Do not auto-commit or roll back residual worktree state on termination; do not alter worktree cleanup semantics.
- Do not change the 60-minute default or add budget-exhaustion auto-resume.
- Do not add silent/quiet-threshold detection (provider-error policy already covers provider-side hangs).
- Do not special-case the AgentJob execution path; it inherits this via #410.

## Decisions

### D1 — The deadline is declared on `RuntimeTurnRequest`, not on a new signal argument

Add an optional `deadlineMs?: number` to `RuntimeTurnRequest` (`packages/runner/src/runtime/opencode/types.ts`). The runtime treats it as the sole deadline source and layers it onto the abort signal internally (D3). The external `signal` argument keeps its current meaning: external cancellation only (runner shutdown, user cancel, parent work-level abort).

**Alternatives considered:**

- *Add a third `deadlineMs` argument to `runTurn(request, signal, deadlineMs)`.* Rejected: the deadline is a property of the turn, not a per-call knob. Putting it on the request keeps the call site stable and lets the warning scheduler read it from the same place.
- *Put `deadlineMs` on `TurnExecutionDeps` (runtime-level config).* Rejected: the deadline varies per turn (different tasks, different overrides); a runtime-level default would re-introduce a hidden global.
- *Keep the deadline purely on the signal (caller layers `timeoutSignal`).* Rejected: the runtime then cannot know when to warn. Two sources of truth (signal timer + request field) would also be a footgun.

A request with `deadlineMs === undefined` (omitted) means "no deadline declared" — no warning, no internal timer, the runtime just awaits the prompt and honours `signal` for external cancel. This is the opt-out path for housekeeping prompts and follow-ups that intentionally run without a deadline.

### D2 — The Action resolves the deadline from `with.timeout` (ms), default 60 minutes

`actions/opencode.ts` reads `numberInput(context.with, "timeout")`; if absent, it uses `DEFAULT_TURN_DEADLINE_MS = 60 * 60 * 1000`. The resolved value is placed on the turn request. This mirrors `core/script` (`actions/registry.ts:91`) and keeps the override channel task-local and discoverable.

We do **not** read the issue-level `agentConfig.timeoutMs` field: that field is ACP-era and is removed by #410. If a future issue wants issue-level deadline config, it can add an `ActionContext.turnDeadlineMs` field later without breaking this contract.

### D3 — The runtime layers the deadline onto the signal via the existing `createTimeoutSignal` helper

Inside `runTurn`, when `request.deadlineMs !== undefined`, the runtime calls `createTimeoutSignal(externalSignal, deadlineMs)` from `system/timeout-signal.ts` and passes `handle.signal` down to `executePrompt`. The handle is disposed in a `finally`. This reuses the layered-timeout helper that already backs `core/script` and `mohist/acp-agent` — no new timer primitive.

Production reads wall-clock via `setTimeout` (transitively, through `createTimeoutSignal` and the warning scheduler). Tests drive both through `vi.useFakeTimers()` (see D6). The `signal.timedOut()` flag from `createTimeoutSignal` is not consumed — the runtime does not need to distinguish timeout-vs-cancel for the result; both surface as `interrupted` per the existing error contract.

### D4 — The warning scheduler is inline in `executePrompt`, single-shot, fire-and-forget

When `deadlineMs` is declared, `executePrompt` schedules exactly one `setTimeout` whose delay is:

```
warningDelayMs = deadlineMs > WARNING_WINDOW_MS
               ? deadlineMs - WARNING_WINDOW_MS
               : 0
```

where `WARNING_WINDOW_MS = 5 * 60 * 1000`. The callback:

1. Flips a local `warned` flag (the per-Prompt-execution guard; retries/restarts get a fresh flag).
2. Calls `client.session.promptAsync({ path: { id: sessionId }, query: { directory }, body: { parts: [{ type: "text", text: WARNING_TEXT }] } })` without awaiting — `void`-ed into a `try/catch` that swallows rejection and emits a single `info`-level `RuntimeDiagnostic` (`code: "deadline-warning-injection-failed"`). Injection failure never fails the turn and is never retried.
3. Is cleared in the same `finally` that disposes the timeout handle. If the prompt completes before the timer fires, the warning is cancelled and never injected (the turn ended on its own; no warning needed).

**Why `setTimeout(0)` for the short-deadline case instead of calling `promptAsync` synchronously:** the prompt call is initiated one statement earlier (`args.client.session.prompt({...}).then(...)`); a `setTimeout(0)` callback fires on the next event-loop tick, giving the HTTP submission of the prompt a head start so the warning lands as a follow-up rather than racing the prompt body. This matches how user follow-ups are expected to arrive.

**Alternatives considered:**

- *A separate `WarningScheduler` class.* Rejected: the state is one boolean and one timer id; a class adds ceremony without insight. A small named helper `scheduleDeadlineWarning(client, sessionId, directory, deadlineMs): () => void` at module scope keeps `executePrompt` readable without a new abstraction crossing files.
- *Inject the warning by writing directly to the transcript.* Rejected: the design explicitly says the warning enters via the same path as a user follow-up, so it benefits from existing event projection and UI plumbing. No special-case transcript entry kind is introduced.

### D5 — The warning text is a runtime-owned constant, task-agnostic

```
You will be interrupted in approximately 5 minutes.
Stop starting any new work now. Commit your current changes,
leave a progress record in this task's progress channel,
and end the turn.
```

Exported as `DEADLINE_WARNING_TEXT` from `turn.ts` so tests assert against the symbol, not a magic string. The text:

- Names no marker (`unfinished`, `promise`), no file (`progress.txt`), no artifact path, no profile identifier.
- Prescribes a deterministic sequence (stop → commit → record → end) and nothing else: no commit message, no file location, no completion marker.
- Is identical for every Prompt execution; per-task wrap-up contracts stay in each task's own prompt (the 7 build/fix builtin prompts already carry the "may be interrupted, commit as you go" resident line from commit `37dd826f6`).

The "approximately 5 minutes" phrasing is intentional: it is a static human-readable signal, not a precise clock. The agent has no reliable clock; the warning's job is to trigger the wrap-up sequence, not to start a countdown.

### D6 — Tests drive the protocol through `vi.useFakeTimers()`, matching the #409 pattern

The existing deadline-abort test in `packages/runner/tests/opencode-runtime-turn.spec.ts:357` already uses `vi.useFakeTimers()` + `vi.advanceTimersByTimeAsync(...)`. We reuse that pattern for the new tests:

- `advanceTimersByTimeAsync(deadlineMs - WARNING_WINDOW_MS)` ⇒ assert `client.session.promptAsync` called once with `DEADLINE_WARNING_TEXT`.
- `advanceTimersByTimeAsync(WARNING_WINDOW_MS)` ⇒ assert `client.session.abort` called and result kind is `interrupted`.
- For the short-deadline case, `advanceTimersByTimeAsync(0)` flushes the immediate timer.

`vi.useFakeTimers()` is per-test in vitest; it does not bleed across workers. The fake SDK client (`buildRuntime` helper) gains a `sessionPromptAsync` mock alongside the existing `sessionPrompt`/`sessionAbort` mocks.

**Alternative considered:** a hand-rolled `Clock`/`Scheduler` injected through `TurnExecutionDeps`. Rejected as over-engineering — the existing sanctioned TS pattern per `AGENTS.md` is `vi.useFakeTimers()`, and the production path has no need to swap clocks.

### D7 — Smoke record format follows #409's JSON pattern

A new `openspec/changes/issue-439/deadline-warning-smoke.json` records: OpenCode CLI/SDK version, the injected prompt body, the running turn's session ID, observed transcript evidence that the running turn processed the injected message (e.g. a tool call or assistant message that references the warning), and the wall-clock pickup latency. Format mirrors `openspec/changes/issue-409/sdk-smoke-verification.json`. The smoke is captured once during implementation; it is not part of the default test suite.

## Risks / Trade-offs

- **[Risk] `promptAsync` ordering vs. the prompt body** — if the warning lands before the prompt is processed, the agent sees the warning first. *Mitigation:* `setTimeout(0)` for the at-turn-start case (D4); for the scheduled case the prompt is already in flight by 5-minutes-to-deadline. OpenCode's session model treats `promptAsync` as a follow-up enqueue, so even a near-simultaneous injection is ordered after the in-flight prompt.
- **[Risk] Long tool call delays pickup past the deadline** — the warning is injected but not processed before abort. *Mitigation:* explicitly accepted by the issue and the design doc; worst case degrades to today's direct termination. No code change; documented in the spec.
- **[Risk] `vi.useFakeTimers()` interferes with fake-client internals** — if any fake in the test starts a real timer, fake timers would stall it. *Mitigation:* the existing fakes are synchronous or use `setImmediate`/microtasks; no `setTimeout` inside the fakes. The new `sessionPromptAsync` mock follows the same convention.
- **[Risk] Deadline introduced for the first time on the OpenCode path** — turns that previously ran unbounded now fail at 60 min. *Mitigation:* 60 min is the documented default and matches user expectation; the warning gives the agent 5 min to wrap up. Rollback is a single revert (see Migration Plan).
- **[Risk] Warning text drift** — future edits could accidentally introduce task-specific wording. *Mitigation:* the text is a single exported constant with a spec scenario pinning the "no markers / no filenames" invariant; a unit test asserts the constant against a regex blacklist (`/unfinished|promise|progress\.txt|\.md$/` etc.).
- **[Trade-off] Two `setTimeout` timers (deadline + warning) instead of one** — slightly more to dispose. Accepted: the alternative (compute remaining time in a single poll loop) reintroduces wall-clock polling, which the spec forbids.

## Migration Plan

This change is additive at the boundary and behaviour-altering only in one respect (OpenCode Workflow turns now have a 60-min backstop they previously lacked).

**Deploy:**

1. Land `deadlineMs` on `RuntimeTurnRequest` (optional field — existing callers unaffected).
2. Land the warning scheduler in `turn.ts` (no-op when `deadlineMs` is absent).
3. Update `actions/opencode.ts` to declare the deadline on every Workflow Inline Agent turn.
4. Capture the smoke record and commit it under `openspec/changes/issue-439/`.

**Rollback:** revert the commit. Turns go back to unbounded execution (pre-change behaviour). No persisted state migration is needed because the deadline is not stored — it is computed at dispatch time from `with.timeout` or the default. AgentJob is untouched by this change.

**Compatibility:** persisted inputs and AgentSession bindings are unchanged. The `with.timeout` knob already exists on `core/script` and is a recognised user-facing input; reusing it introduces no new Workflow schema field. When #410 routes AgentJob through `OpenCodeRuntime`, AgentJob turns automatically inherit the protocol with no further change here.

## Open Questions

- **Should the smoke run against a long-running tool call to demonstrate pickup delay?** Not required by the issue's acceptance criteria (which only ask for evidence that a mid-turn `promptAsync` is picked up). The smoke can use a simple prompt that sleeps inside a tool. If a follow-up wants stronger evidence, it can extend the record later.
- **Should the warning injection diagnostic be surfaced to the Workflow task log?** Currently planned as a `RuntimeDiagnostic` returned in the turn result's `diagnostics` array (already wired through to the Action output). No additional task-log plumbing. Revisit if users report difficulty debugging missed warnings.
