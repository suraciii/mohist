# Self-Review - Issue 601

Review round: re-review.
Review basis: issue 601 from `mo issue view 601 --project proj_f6c141d63b6243bfbb481737b2243b87`, including the issue body and all current comments; the current `proposal.md`, `design.md`, `tasks.json`, and complete capability spec; and the relevant runner/server lifecycle code. The existing review was a first-round FAIL with five must-fix findings. This review verifies those dispositions and checks for regressions.

## Verdict

PASS. No must-fix problems remain; the plan is ready to build.

## Previous Findings

- **F-001, fail-closed migration: fixed properly.** The compatibility mode and legacy settlement path were removed. `design.md:83,90-94` defines one v1 transition with dispatch quiescence, capability admission, rejection of plain legacy settlement, and non-settling `boundary-missing` reconciliation. `tasks.json:94-98` makes those rules and their end-to-end tests owned by T-005. This satisfies the issue requirement that invalid or legacy reports cannot bypass the durable boundary or manufacture success/failure from session activity.
- **F-002, concrete dirty/unconfirmed state and actions: fixed properly.** `design.md:61-70` maps `committed-clean`, `dirty`, and `unconfirmed` to persisted task/stage/run lifecycle values, wire states, dispatch behavior, recovery actions, clean-verification completion, and explicit stop behavior. The same contract is normative in `spec.md:76-92` and owned by T-003 at `tasks.json:49-56`. Dirty and unconfirmed valid Action results remain Running and recoverable, while conclusive Action failures retain the existing failed path.
- **F-003, later verification versus immutable receipt: fixed properly.** `design.md:31-35` defines `WorkspaceVerification` as separate mutable evidence, and `spec.md:146-155` gives it its own identity, boundary fingerprint, idempotency, conflict, generation, and source-adoption rules. The recovery scenario no longer submits a replacement receipt (`spec.md:172-176`), and T-004 explicitly tests later verification without replacing the initial receipt (`tasks.json:77-78`).
- **F-004, legal uncommitted task-source recovery: fixed properly.** `design.md:53` and `spec.md:118-138` define the authenticated operator, exact-generation fence, explicit source allowlist, path disjointness rules, path-limited commit, rejection behavior, and mandatory follow-up verification. T-004 owns the operation and preservation tests (`tasks.json:70-78`). Rejected or failed adoption preserves source, output, artifact, and unrelated files.
- **F-005, early Workflow failure exits: fixed properly.** The boundary wrapper is placed at the outer Workflow-task adapter and covers workspace setup, Action resolution, input/dispatch validation, branch probes, Action throw/normalization, artifact/output/set-variable failures, and the outer catch (`design.md:74-76`). Pre-Action failures receive `actionStarted=false` and an unavailable-probe reason, while known Action results are retained for later capture/projection failures. T-002 lists every required exit and deterministic test (`tasks.json:29-35`), matching the complete-path requirement in `spec.md:1-25` and `spec.md:204-220`.

## Regression Check

The fixes do not introduce a must-fix regression. The fail-closed migration is consistent with the issue's latest review note. The new recovery state is kept distinct from the existing Agent result settlement model rather than adding Git fields to it (`design.md:70`), while the plan still accounts for the current `WorkflowReportService`, `ReceiveTaskReportAsync`, `WorkResultJournal`, `WorkExecutor`, and Pi/OpenCode runtime paths. The plan also preserves the required distinction between valid Action failure and workspace uncertainty, so only cleanup-induced dirty or unconfirmed evidence is prevented from becoming a business failure.

## Dimension Verdicts

- **Issue grounding: checked, no issue.** The P1 user goal, required durable boundary, three workspace outcomes, scoped cleanup, recovery/fresh-generation behavior, exact replay/conflict behavior, runtime coverage, and latest four contract-gap comments were read before judging the artifacts.
- **Coverage: checked, no issue.** The proposal states each issue goal, the capability spec gives normative requirements and scenarios for each one, and T-001 through T-005 assign implementation and deterministic test coverage across runner, server, recovery, migration, generic Actions, Pi, and OpenCode.
- **Correctness: checked, no issue.** The approach persists the immutable pair before cleanup/report settlement, makes dirty and unconfirmed nonterminal and fenced, permits only scoped recovery, separates later observations from the initial receipt, rejects conflicting identities, and keeps conclusive Action failures on the existing failure path.
- **Consistency with the current codebase: checked, no issue.** The plan targets the existing journal atomic-write boundary, outer executor early returns, report translator/admission path, Workflow aggregate lifecycle, current status mapping, workspace registry/markers, and Agent settlement patterns. It identifies the required changes without treating runtime transcripts or the existing cleanup loop as authoritative.
- **Task breakdown: checked, no issue.** T-001 establishes shared types and journal persistence; T-002 and T-003 independently build runner arbitration and server settlement; T-004 depends on both for fenced recovery; T-005 depends on the completed v1 contract for migration. The dependency graph is acyclic, and each task has acceptance criteria and deterministic tests tied to a spec anchor.

## Observations

- The plan still leaves canonical serialization details for boundary fingerprints open: JSON property ordering, path ordering, artifact-reference normalization, and diagnostic truncation should be fixed before implementation. This is an implementation precision concern; the exact replay/conflict behavior and tests are already required.
- Default lease duration, cleanup work budget, recovery deadline, clean-boundary cleanup timing, and evidence retention remain open in `design.md:96-100`. The issue requires them to be bounded and exposed, but does not require a particular configuration value or retention policy.
- T-003 owns the Workflow recovery state/action map while T-004 owns the concrete verification and source-adoption operations. The dependency order is usable, but implementation ownership should be kept explicit so T-003 does not create a second verification contract.
- The proposal mentions CLI/Web status consumers, while the task list explicitly requires server/API status projections and does not name a separate client task. The issue acceptance criteria require recoverable status exposure, not a specific client label or UI workflow, so this is a follow-up integration concern rather than a must-fix problem.

<promise>PASS</promise>
