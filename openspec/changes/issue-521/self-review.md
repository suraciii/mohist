# Self-Review — issue-521 (re-review after fixes)

Re-reviewing `proposal.md`, `design.md`, `tasks.json`, and `specs/` against issue 521
after the F1–F5 fixes. Acting as reviewer only; no files changed other than this one.

## Status of prior findings

- **F1 (lease lifecycle / concurrent-followup rejection)** — RESOLVED. Design D8
  reconciles it: acceptance collapses Begin+Confirm into one synchronous step that
  records an already-Accepted lease; `FollowupOperationInProgressException` is
  retired; the lease becomes per-turn (many coexist); the recovery-idle guard is
  redefined as "no non-terminal follow-up turn". Turn spec adds "Follow-up is never
  rejected merely because a turn is in flight"; T-001 has matching criteria.
- **F2 (un-specced redelivery "drain" promise)** — RESOLVED. The Risks bullet no
  longer claims a server-side drain; it references D9 (client retry). The resolved
  redelivery Open Question was removed.
- **F3 (retry re-dispatch semantics)** — RESOLVED. D9 + input-spec scenarios
  ("Retry re-attempts delivery only while the turn is still queued"; "Retry against
  an executing or terminal turn is identity-only") + T-001 criterion.
- **F4 (presentation de-duplication)** — RESOLVED. D7 states the observation is
  status/identity-only; call spec adds the no-double-render requirement; T-002/
  T-003/T-004 carry de-dup criteria.
- **F5 (capacity threshold)** — RESOLVED. D8 pins a bounded queued-input/turn
  capacity enforced at acceptance (reject, not drop) as a runtime constant; an Open
  Question captures picking the value; T-001 has a capacity-rejection criterion.

## Coverage of issue acceptance criteria

All six ACs map to spec requirements and task criteria:

- AC1 follow-up after launch terminates — input spec "Follow-up does not create an AgentJob".
- AC2 SessionInput + start/join AgentTurn, no new AgentJob — input + turn specs.
- AC3 during-execution accept & queue, no interrupt/drop/merge — turn spec
  "Queueing during execution without interruption" + "never rejected merely because
  a turn is in flight".
- AC4 distinguish accepted-pending vs executing — turn spec "Distinct input
  acceptance and turn execution state"; call spec.
- AC5 idempotent retry returns original input — input spec "Idempotent retry" +
  retry re-dispatch scenarios; call spec transport.
- AC6 Web & CLI same status — call spec "Shared status interpretation".

Issue Non-Goals (cancel, attachments, Slack, compaction) are respected in the
proposal/design Non-Goals.

## Format & structure checks

- All three specs use `### Requirement` / `#### Scenario` (4/4 requirements each,
  9/9 scenarios each); SHALL/MUST language; no ADDED/MODIFIED/REMOVE headers; every
  requirement has ≥1 scenario.
- tasks.json is valid JSON; DAG is acyclic and every `dependsOn` points to a strictly
  lower priority (T-002→T-001, T-003/T-004→T-002). No standalone test tasks.
- Design factual claims about the current code remain accurate.

## Non-blocking observations (not FAIL-worthy)

- **operationId vs runner operation-journal on retry re-dispatch.** D5 correlates
  follow-up turns by a stable `operationId`; D9 re-dispatches on retry while the
  turn is queued. The runner's `FollowupOperationJournal` dedupes by
  `(sessionKey, operationId)` (`followup-handler.ts:179`), so a naive retry that
  reuses the same `operationId` would be a no-op at the runner. The intended
  *behavior* is correctly specified (input-spec "Retry re-attempts delivery only
  while the turn is still queued") and is caught by T-001's criterion "A retry with
  the same key re-attempts delivery when the turn is still queued", so a no-op
  re-dispatch would fail that test and be corrected during implementation (e.g. a
  fresh dispatch `operationId` per attempt, with the turn tracking the latest). This
  is an implementation mechanism, not a plan defect, and is consistent with how the
  design defers other mechanism details to Open Questions.

## Verdict

All prior must-fix and should-fix findings are resolved. The plan is internally
consistent, all six acceptance criteria are covered by specs and tasks, the spec
format is compliant, and the task graph is valid. Ready to build.

<promise>PASS</promise>
