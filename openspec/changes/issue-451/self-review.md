# Self-Review — Issue 451 (Pi Session Commands)

Reviewer verdict: the plan is internally coherent and the central technical
claim is verified, but two design defects would make the built feature violate
its own spec/acceptance criteria. They must be fixed before building.

## What was verified and is sound

- **The "OpenCode compact/reset is already Unavailable in production" claim
  (design D4 / Risks) is correct.** Traced end-to-end: the canonical routes
  (`AgentSessionRecoveryRoutes.ExecuteCompactAsync` / `ExecuteResetAsync`)
  dispatch via `RunnerSessionCommandDispatcher` → SignalR `SessionCommand`
  (`RunnerSessionCommandDispatcher.cs:40-50`); the live Runner registers no
  `SessionCommand` handler (`registerSessionCommandHandler` has no call site;
  `runner-signalr.ts:152-178` registers follow-up/cancel/workspace/workflow
  only), so the invocation resolves to null → `Unavailable()`
  (`RunnerSessionCommandDispatcher.cs:50`) → `MapCommandResult` returns 503
  `runner_unavailable` (`AgentSessionRecoveryRoutes.cs:203-207`). The grain's
  server-side summary `CompactAsync(CompactAgentSessionCommand)` has no route
  caller. So wiring a handler whose `opencode` branch returns `unavailable`
  preserves the semantic outcome. D4 is safe and the non-goal is respected.
- **Wire is already runtime-aware.** Every command payload already carries
  `runtime` from the persisted binding, so D1 (drop the grain `pi` throw) needs
  no wire/DTO/route change. Confirmed.
- **Task graph is a valid DAG.** `T-001`→{`T-002`,`T-003`}→`T-004`; every
  `dependsOn` points to a strictly-lower-priority task; each task bundles its
  tests; the prep/routing/channel split is reasonable.
- **Spec↔issue coverage** of the four commands and the missing-session Reset
  hint is complete and uses correct normative/scenario structure.

## Blocking findings (must fix)

### F1 — Cancel "interrupt-unconfirmed" cannot reach the user; the spec is unmet

`pi-session-channels/spec.md` requires: when stop cannot be confirmed, "the Pi
runtime SHALL report the cancel as interrupt-unconfirmed … SHALL NOT report
the turn as safely stopped." This maps to the issue acceptance criterion
("无法确认停止时报告中断未确认，而不是显示为已安全停止").

Design D6 proposes to carry interrupt-unconfirmed as a **diagnostic** on an
otherwise-`cancelled` result, and states the cancel handler maps the result
into its taxonomy **unchanged**. But the handler reply shape has no field for
diagnostics:

- `CancelAgentSessionReply` is `{ state: string }` only
  (`packages/runner/src/server/session-target.ts:148-150`).
- `cancel-handler.ts:85-88` maps `facts.cancelled === true` →
  `{ state: "cancelled" }` and discards `result.diagnostics`.

So under the current design a stop-unconfirmed Cancel is reported to the API/
user as `cancelled` (i.e. safely stopped) — the exact behavior the spec
forbids. Neither D6 nor any task addresses surfacing interrupt-unconfirmed to
the reply/API. This must be reconciled: either extend the reply (and server
DTO/HTTP response) to carry an interrupt-unconfirmed signal/state, or change
the runtime result shape so the handler emits a distinct state. The spec, D6,
and T-002/T-004 acceptance criteria all need to reflect whatever is chosen.

### F2 — Idle Follow-up is not serialized against workflow turns / other follow-ups (double-prompt race)

The Pi SDK permits one in-flight `prompt()` per physical session. Workflow
turns are serialized per logical session by `WorkflowSessionTurnCoordinator`
(`workflow-session-turn-coordinator.ts`, keyed by
`projectId/workflowRunId/sessionName`, acquired only by the workflow/check
executor via `withTurn`). The Follow-up path is invoked directly from the
SignalR handler (`followup-handler.ts` → `runtime.followup`) and **does not
acquire that coordinator**, nor any per-session prompt mutex in `PiRuntime`.

Design D5 decides busy/idle from `session.isStreaming` and the Risks section
claims "the `isStreaming` branch naturally routes a Follow-up to steer while a
Workflow turn runs, avoiding a second prompt." That mitigation only covers the
case where a workflow turn is **currently streaming**. It does **not** cover:

- A workflow turn **queued** in the coordinator (predecessor still running).
  While the predecessor streams `isStreaming` is true, but in the gap between
  the predecessor's `prompt()` resolving (`isStreaming`→false) and the queued
  turn's `prompt()` starting, an idle Follow-up sees `isStreaming === false`
  and starts its own `prompt()` → two prompts collide on the same Pi session.
- Two concurrent idle Follow-ups on the same session (both see idle, both
  call `prompt()`).

This race is Pi-specific: OpenCode Follow-up targets a separate OpenCode
server process that serializes prompts itself; Pi executes in-process in the
Runner with no equivalent serializer for non-workflow prompts. The plan must
define where the one-prompt-per-session invariant is enforced for Pi (e.g. a
per-physical-session prompt mutex in `PiRuntime` covering `runTurn` and the
idle Follow-up path, and/or having the idle Follow-up respect the coordinator),
and add a scenario/criterion covering it. Currently neither spec nor any task
addresses it.

## Non-blocking observations

- **N1 (factual nuance, D4/Risks).** The design says wiring the handler changes
  OpenCode latency from a "15 s transport timeout" to immediate `unavailable`.
  For an unhandled SignalR client method the invocation typically resolves to
  null promptly (not a full 15 s); the 15 s is only a backstop. The conclusion
  (semantic outcome unchanged) holds regardless, but the "15 s timeout" framing
  is imprecise.
- **N2 (wording tension).** `pi-session-command-routing/spec.md` says OpenCode
  commands "SHALL behave **identically** to before this change," while D4
  intentionally changes the response path (timeout→explicit `unavailable`).
  "Identically" should be softened to "same semantic outcome" or the spec
  should state the latency equivalence explicitly, to avoid a literal-spec vs
  design conflict at implementation time.
- **N3 (recovery reconciler unspecified).** D4 reuses the `SessionCommand`
  scaffolding "unchanged — we only supply the handler," but does not define a
  `reconcileStarted` reconciler for Pi compact/reset. On Runner restart with a
  journaled "started" Pi operation, a null reconciler yields "indeterminate" →
  `unavailable`, relying on server-side recovery for redelivery. That is
  probably safe, but the issue's Domain Model explicitly calls out the
  "definitely not started vs may-have-started-unknown" distinction and
  "preserve the original operation" rule; neither spec restates it for Pi.
  Recommend a sentence in the routing spec confirming the inherited
  at-most-one-effect behavior for Pi.
- **N4 (open questions are acceptable).** The `preflight` / `compact()` SDK
  surface being unverified is correctly gated behind T-001's smoke (design D9,
  Q1/Q2), and the idle-Follow-up turn budget (Q3) / OpenCode compact/reset
  ownership (Q4) are reasonably deferred. No action needed for the plan.

## Conclusion

F1 and F2 are real defects that would cause the implemented feature to violate
its own spec/acceptance criteria (Cancel honesty; one-prompt-per-session
invariant). They are design/spec gaps, not implementation details, and should
be resolved in `design.md` / the specs / `tasks.json` before execution begins.

<promise>FAIL</promise>
