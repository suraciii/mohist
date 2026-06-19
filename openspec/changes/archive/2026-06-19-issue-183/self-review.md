# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: blocking
  Scope: consistency
  Evidence: The spec scenario "A task status transition does not mutate the workflow status" stated the transition "SHALL only mutate the TaskRun aggregate's own state." This is false for `FailTask` (`WorkflowRun.Task.cs:49`), which sets `run.Status = Failed`, `current.Status = StageRunStatus.Failed`, and `current.Failure` on task failure. The literal wording would force a builder to either write a failing test or change `FailTask` (a Non-Goal). Design D4 had already reconciled this, but the spec text was not aligned. Split the scenario into (a) "A non-terminal task status transition does not recompute the workflow status" (Pending/Running/Completed only mutate the task) and (b) "Task failure is a workflow policy reaction, not a status derivation" (no status-sync-from-tasks path exists). The requirement intent ("互不推导" = no functional derivation) is preserved.
  Verification: `npx openspec validate issue-183 --strict` → "Change 'issue-183' is valid"; the repaired scenarios now match `FailTask` behavior and design D4.

- [ID: item-2]
  Severity: info
  Scope: dependencies
  Evidence: T-002 and T-003 (priority 2, non-first tasks) had empty `dependsOn`, leaving "every non-first task has appropriate dependsOn" unsatisfied. Both tasks' doc comments reference the single-runner claim invariant / authoritative `Claim.RunnerId` that T-001 establishes and makes testable. Added `dependsOn: ["T-001"]` to both, noted in each task that it is a documentation dependency (not a hard build dependency).
  Verification: DAG/priority/cycle check passes — deps `[["T-001",1,[]],["T-002",2,["T-001"]],["T-003",2,["T-001"]]]`, acyclic, all deps point to strictly lower priority.

- [ID: item-3]
  Severity: info
  Scope: completeness
  Evidence: T-001 implements two spec requirements (single-runner claim invariant + status independence) but its `spec` field cited only one, so the status-independence requirement was not traceable via the `spec` field. Added an explicit note in T-001 listing both requirement anchors it satisfies (keeping the single-string `spec` convention seen in issue-110).
  Verification: T-001 notes now cite both `#single-runner-claim-invariant` and `#workflowrun-status-and-taskrun-status-are-independent-state-machines`.

- [ID: item-4]
  Severity: info
  Scope: completeness
  Evidence: T-002's acceptance only scoped the "no ownership wording" check to `AgentSessionQuerier`, but issue acceptance criterion 1 is repo-wide. Confirmed via `rg` that `WorkflowRunOwnsSession` is the only session-ownership expression in `packages/server/src` (other "owns" hits are feedback/connection ownership, unrelated). Strengthened T-002's acceptance to include a repo-wide search verification.
  Verification: `rg -ni "owns?.*session|session.*owns?" packages/server/src` returns only the querier method.

## Blocking Items

_None._

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: alignment
  Evidence: Issue Product Shape point 4 ("issue 状态到 workflow runtime status 的推导，区分 queued 与 active，经审视方向正确，无需修改") is a reviewed-and-correct no-op. It is correctly absent from the proposal/tasks (no work needed), but neither the proposal nor design explicitly records that this review was performed.
  SuggestedAction: Add a one-line acknowledgment in proposal.md or design.md that point 4 was reviewed and confirmed correct, for traceability. Non-blocking.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: completeness
  Evidence: Design D3 defers an AgentSessionQuerier DB integration test because no querier test fixtures exist yet; the peer-association contract is verified at the domain level only.
  SuggestedAction: Add a querier integration test once DB test fixtures exist, to guard the association judgment directly.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: feasibility
  Evidence: `ReleaseClaim()` has no callers, so the `_lastKnownRunnerId` fallback in `GetClaimedRunnerIdAsync` is effectively redundant with `Claim?.RunnerId` today (design Open Questions).
  SuggestedAction: Track a separate issue to either remove the dead fallback or wire up claim release, outside this change's scope.
  Status: follow-up

## Traceability Summary

| Issue acceptance criterion | Spec requirement | Task |
|---|---|---|
| No naming/comment implies workflow owns session | AgentSession is a peer aggregate associated by task reference | T-002 |
| Grain cached runner field semantics declared | Cached runner identity has an explicitly declared role | T-003 |
| WorkflowRun.Status / TaskRun.Status independence explicit | WorkflowRun status and TaskRun status are independent state machines | T-001 |
| Single-runner invariant explicit | Single-runner claim invariant | T-001 |
| AgentSession↔WorkflowRun peer association explicit | AgentSession is a peer aggregate associated by task reference | T-002 |

All 4 spec requirements have implementing tasks; all 5 issue acceptance criteria are covered. Proposal Capabilities (Modified: `workflow-run`, no new capabilities) match the delta spec (ADDED Requirements under `workflow-run`). No circular dependencies; task granularity is appropriate (each task delivers a distinct acceptance criterion in a distinct module; the rename+comment work IS the disambiguation deliverable for this issue, not over-granular mechanical splitting).

<promise>PASS</promise>
