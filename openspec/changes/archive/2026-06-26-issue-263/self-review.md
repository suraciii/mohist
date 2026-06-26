# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: Verified all 6 tasks' `spec` references resolve to real requirement headers in `specs/epic-lifecycle/spec.md`. Anchors confirmed: `#Epic lifecycle state machine`, `#Autonomous advancement on terminal issue events`, `#Reliable and idempotent terminal-event trigger`, `#HTTP API epic lifecycle endpoints`, `#CLI epic lifecycle commands`, `#Epic detail page lifecycle actions`. No mismatched references found; no repair needed.
  Verification: `grep "^### Requirement:"` against the spec file produced 15 headers; every task `spec` field matches one of them (modulo anchor whitespace normalization).
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-002's `TryStartNext` calls `IIssueGrain.StartWorkAsync`, which the existing `/issues/{n}/start` HTTP route invokes with a resolved `WorkflowProjectContext` (repo resolution). Whether the grain-to-grain call needs the same context is unresolved (design Open Question Q2). If `StartWorkAsync` requires a non-null context, T-002 will need to resolve the repository context inside the epic grain or via the issue grain itself.
  SuggestedAction: During T-002 implementation, confirm the `IIssueGrain.StartWorkAsync(WorkflowProjectContext?)` signature behavior when passed null. If repo resolution must happen eagerly, push it into `IssueGrain.StartWorkAsync` (preferred — keeps the epic grain free of repo-resolution concerns) or add a repo-resolution helper call in `TryStartNext`.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-003 generalizes `EpicReconciliationService` to sweep `running` epics with `ReconcileAfterTerminalAsync`, but the current default sweep cadence is daily (`EpicReconciliationOptions.DefaultReconciliationPeriod`). A missed terminal event on a `running` epic would delay recovery up to 24h, which undermines the autonomy value proposition (design Open Question Q1).
  SuggestedAction: Introduce a shorter cadence (e.g. 5–15 minutes) for `running` candidates while keeping the daily cadence for `idle` auto-done recovery. Decide during T-003 implementation; can also be deferred to a tuning follow-up since correctness is not affected.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: alignment
  Evidence: The `Manual Mark Done retained` requirement from the existing `epic-lifecycle` spec is intentionally not in the delta (no MODIFIED/REMOVED entry), which correctly implies it is preserved. T-001 keeps `MarkDone` behavior unchanged. This is correct but implicit — a reader scanning only the delta might miss that manual Mark Done still exists.
  SuggestedAction: No action required for plan correctness. Optionally add a one-line note in T-001's description that manual Mark Done behavior is deliberately unchanged, for reviewer clarity.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: consistency
  Evidence: T-001's acceptance criterion wording — "Make Start/Pause/Resume no-ops when already in target state (idempotent at grain boundary; domain stays strict)" — is slightly ambiguous about where idempotency lives. Design D7 clarifies: the domain methods throw on invalid transitions but no-op on already-target-state; the grain boundary additionally catches terminal-state throws to return 409/200. The criterion is interpretable but could be misread as "domain swallows all errors".
  SuggestedAction: No repair needed — design D7 disambiguates. If desired, refine T-001 acceptance to: "Start/Pause/Resume are no-ops at the domain layer when the epic is already in the target state; invalid non-target transitions (e.g. Pause from Idle) throw; the grain boundary maps terminal-state throws to 409."
  Status: follow-up

## Summary

- **Alignment**: All 13 "What Changes" entries in the proposal trace to issue #263's Product Shape, Domain Model, and Acceptance Criteria (AC #1–#15). No issue requirement is missing. Non-Goals (parallelism, external-prereq auto-wake, auto-retry, configurable cancel policy, new blocked state) are respected — none of the tasks implement them.
- **Completeness**: All 15 spec requirements (3 MODIFIED + 12 ADDED) are covered by tasks. Manual Mark Done is preserved by omission (correct). Edge cases covered: cancelled in-progress advance (T-003), failed-issue-holds-epic (T-002), running-but-idle (T-002/T-006), idempotency (T-001/T-002/T-004), race safety (T-002 structural via Orleans).
- **Consistency**: Single modified capability `epic-lifecycle` matches the proposal's Capabilities section. All task `spec` anchors resolve. Design D1–D9 map cleanly onto tasks. Naming (`Idle`/`Running`/`Paused`/`Done`/`Closed`) is consistent across spec, design, and tasks.
- **Feasibility**: Task granularity is by functional slice (domain, grain, events, API, CLI, Web) — no over-split "define interface / register DI / move file" tasks; no standalone test tasks (tests bundled into each). The only implementation uncertainty (WorkflowProjectContext in grain-to-grain start) is flagged as a follow-up, not a blocker.
- **Dependency completeness**: DAG verified — T-001 (p1) → T-002 (p2) → {T-003, T-004} (p3) → {T-005, T-006} (p4). Every `dependsOn` points to an existing task with strictly lower priority. No cycles.

<promise>PASS</promise>
