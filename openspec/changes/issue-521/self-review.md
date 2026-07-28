# Self-Review — issue-521

Reviewing `proposal.md`, `design.md`, `tasks.json`, and `specs/` against issue 521
("在同一个 AgentSession 中持续追问"). Acting as reviewer only; no files changed.

## Coverage of issue acceptance criteria

| AC | Covered by | Verdict |
|---|---|---|
| AC1 follow-up after launch terminates, context retained | input spec "Follow-up does not create an AgentJob" | OK |
| AC2 adds SessionInput, starts/joins AgentTurn, no new AgentJob | input + turn specs | OK |
| AC3 input during execution accepted & queued, no interrupt/drop/merge | turn spec "Queueing during execution without interruption" | OK (spec) — but see F2 |
| AC4 distinguish accepted-pending vs executing | turn spec "Distinct input acceptance and turn execution state"; call spec | OK |
| AC5 idempotent retry returns original input | input spec "Idempotent retry"; call spec transport | OK — but see F3 |
| AC6 Web & CLI same status interpretation | call spec "Shared status interpretation" | OK |

All issue Non-Goals (cancel, attachments, Slack, compaction) are respected in the
proposal/design Non-Goals. Spec format is compliant: `### Requirement` / `#### Scenario`,
SHALL language, every requirement has ≥1 scenario, no ADDED/MODIFIED/REMOVE headers.

## Findings

### F1 — [Must fix] Lease lifecycle and concurrent-followup rejection are not reconciled with synchronous acceptance + multi-turn queueing (correctness risk for AC3)

Today `AgentSessionGrain.BeginFollowupAsync` rejects a second follow-up while one is
in flight: it throws `FollowupOperationInProgressException` if any lease is
non-accepted (`AgentSessionGrain.cs:425-427`), and `EnsureSessionIdleForRecovery`
treats any pending follow-up as "active" (`AgentSessionGrain.cs:570`). The lease is
assumed to be at-most-one and serialized through a Begin→Confirm→clear cycle.

The new model requires the opposite of part of this: AC3 / the turn spec require a
follow-up submitted while a turn is **executing** to be *accepted and queued*, not
rejected. With multi-turn queueing (D3), several follow-up turns can be queued or
executing at once, so there can be several in-flight leases — breaking the
"at-most-one / reject-if-non-accepted" assumption.

The design never reconciles this:
- D4 (synchronous acceptance) does not state whether the Begin/Confirm two-step is
  collapsed into one synchronous accept (which would eliminate the non-accepted
  window) or retained.
- D5 says "the lease gains InputId/TurnId" and is cleared on idle `session.activity`,
  i.e. the lease now spans the whole turn lifetime and there can be many — but the
  existing rejection guard (`FollowupOperationInProgressException`) and the
  recovery-idle guard (`GetPendingFollowups().Count > 0`) are not addressed.

Risk: an autonomous builder following the existing grain + the design as written
could keep the rejection and *break AC3*, or keep the single-lease assumption and
deadlock multi-turn queueing. The design must state explicitly: (a) whether
acceptance collapses Begin+Confirm; (b) that the concurrent-followup rejection is
removed/relaxed to allow queueing during execution; (c) how the at-most-one lease
invariant becomes per-turn under multi-turn queueing; and (d) how the recovery-idle
guard behaves with queued (non-terminal) follow-up turns.

### F2 — [Must fix] Design promises a redelivery "drain" that is excluded from Non-Goals and absent from specs/tasks (internal inconsistency)

D4/Risks state: "The next accepted follow-up drains queued turns on the same
dispatch." But:
- Non-Goals explicitly exclude "Auto-redelivery of queued inputs across an extended
  runner outage."
- No spec requirement covers draining, and no task acceptance criterion mentions it.

So the design's stated mitigation is neither specced nor tasked. An autonomous
builder working from the tasks will not implement draining, leaving an input
accepted-while-runner-offline stuck in `queued` indefinitely — contradicting the
design's own mitigation sentence. Either add a task/spec requirement for the drain,
or remove the "drains queued turns" claim from the design and state plainly that a
queued input stays queued until the runner is reachable again (honest, matches the
Non-Goal).

### F3 — [Should fix] Idempotent-retry re-dispatch semantics are unspecified

The input spec ("Idempotent retry resolves to the same input") and call spec
("Idempotent retry returns the original identity") cover *identity* (same Input, no
duplicate) but are silent on *delivery*: when a retry with the same key hits an
already-accepted input whose turn is still `queued` (e.g. original dispatch failed
offline), does the retry re-attempt delivery? This interacts directly with F2. The
specs/design should state whether retry is pure-identity (no re-dispatch) or also
re-triggers delivery, so AC5's "retry returns the original Input" has unambiguous
runtime behavior.

### F4 — [Should fix] Presentation de-duplication between the new Inputs/Turns observation and the existing transcript is not addressed

D1 keeps the runner writing a flat `session.input` transcript event *and* the grain
holding a `SessionInput` subrecord for the same follow-up. D7 exposes the
`Inputs`/`Turns` lists. Neither the design nor the Web/CLI tasks address how clients
avoid rendering the same follow-up message twice (once from the inputs list, once
from the transcript). The launch path may already solve this for the first input, but
it should be confirmed/stated so T-003/T-004 have an explicit de-duplication rule.

### F5 — [Minor] Capacity threshold is referenced but undefined

The input spec ("at capacity ... reject") and D6 ("capacity exceeded" → rejected)
both reference a capacity bound, but no design decision or task defines the value or
where it is enforced. The design doc treats capacity as a runtime parameter, so this
is acceptable in principle, but a task should at least pin *that* a bound is enforced
and roughly where, so "rejected on capacity" is testable rather than aspirational.

## Other notes (not blocking)

- D2 (optional idempotency key, recovery convention) is a defensible documented
  choice; the issue wording "与启动一样带稳定调用身份" could be read as
  "required like launch", but the alternative is recorded. Acceptable.
- T-001 mapping to two specs (input + turn) is cohesive (shared accept transition)
  and its acceptance criteria cover both; not over-granular. OK.
- tasks.json is valid JSON, DAG is acyclic, every `dependsOn` points to a strictly
  lower priority (T-002→T-001, T-003/T-004→T-002). OK.
- Design factual claims about the current code (EnsureInitialLaunch,
  BeginFollowupAsync lease, operationId-correlated event clearing at
  `AgentSessionGrain.cs:924`, binary `sent` result, no idempotency key) are accurate
  against the codebase.

## Verdict

F1 and F2 are problems that must be fixed before building: F1 is a correctness risk
(the existing concurrent-followup rejection conflicts with a hard AC and the lease
lifecycle is unreconciled under synchronous acceptance + multi-turn queueing), and F2
is an internal inconsistency (a mitigation promised in the design but excluded from
Non-Goals and absent from specs/tasks). F3–F5 should be addressed alongside.

<promise>FAIL</promise>
