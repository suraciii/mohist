# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: completeness
  Evidence: The `issue-prerequisites` capability spec contains a MODIFIED requirement ("Waiting for delivery is not a failure state") and a REMOVED requirement (eligibility summarization), but no task referenced that spec and no acceptance criterion explicitly preserved its key behavior (a waiting issue must not become `blocked` or a failure state). T-002 implements that work but its `spec` field pointed only at `http-api`.
  Verification: Added an acceptance criterion to T-002 asserting existing prerequisite blocking is preserved as the `WaitingFor(Issue)` blocker and that waiting-for-delivery does not set blocked/failure status; extended T-002's notes to state it delivers the `issue-prerequisites` delta; added a "waiting-for-delivery does not set blocked status" test expectation. Re-validated `tasks.json` parses and the dependency graph remains a DAG.

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: The issue's "behavior lives on the Issue" principle is slightly idealized — `CanStart` is not a pure function of the Issue's own state because prerequisite *delivery* is other issues' state. The design handles this by feeding the undelivered set into `Issue.StartBlocker(...)`, but the `issue-start-readiness` spec scenarios describe derivation purely from "IsDraft + prerequisites" without noting delivery is an input.
  SuggestedAction: During implementation (T-001), add a spec scenario or note clarifying that prerequisite delivery status is supplied to the Issue and the Issue owns blocker precedence, so the spec matches the cross-aggregate mechanism.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: Edge cases for `SetDraft` after an issue has started (e.g. an `InProgress` or reopened issue) and the display value of `canStart`/`blocker` for already-running issues are not explicitly specified. Current specs constrain draft toggling to "not yet started" issues but do not state the refusal behavior.
  SuggestedAction: Decide and add a scenario for `SetDraft` refusal semantics once started, and clarify `canStart` rendering for non-backlog issues, during T-001/T-002.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: consistency
  Evidence: Minor naming variation between the typed domain name (`IssueStartBlocker`, query `StartBlocker`) in the design/tasks and the wire/spec name (`blocker`) in proposal/specs. Both are internally consistent within their layer and the design lists the exact wire shape as an open question.
  SuggestedAction: Confirm the final `blocker` JSON shape (`{kind:"draft"}` vs `{kind:"waiting-for", issue:{...}}`) when T-002 lands and align the spec example if needed.
  Status: follow-up

## Summary

Reviewed proposal, design, all five capability specs, and tasks.json against the issue. Alignment: every issue acceptance criterion (IsDraft default draft; draft blocks start with "still a draft"; derived canStart/blocker on the Issue; board/detail visual distinction; `IssueStartEligibility` removed; prerequisite blocking as `WaitingFor(Issue)`) traces to proposal "What Changes", a spec requirement, and a task. Completeness: all five proposal capabilities have spec files; every spec is covered by a task (issue-prerequisites now explicitly via T-002 after item-1). Consistency: capability names, field names (`isDraft`/`canStart`/`blocker`), and the `Draft | WaitingFor(Issue) | none` sum are consistent across artifacts. Feasibility: task granularity is by functional module (domain / server cutover / web-ui / CLI), no over-fine "define interface"/"register DI"/standalone-test tasks, tests live inside each task. Dependencies: T-001 → T-002 → {T-003, T-004} is a valid DAG with every `dependsOn` pointing to a strictly-lower-priority existing task and no cycles.

<promise>PASS</promise>
