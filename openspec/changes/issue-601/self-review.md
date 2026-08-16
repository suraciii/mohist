# Self-Review - Issue 601

Review round: first review
Review basis: issue 601 via `mo issue view 601 --project proj_f6c141d63b6243bfbb481737b2243b87`, including the issue body and comments; `proposal.md`, `design.md`, `tasks.json`, and the complete spec were read before this review. No prior `self-review.md` exists.

## Verdict

FAIL. The plan has must-fix problems relative to the issue contract and is not ready to build.

## Must-Fix Findings

### F-001: The migration plan contradicts the issue's fail-closed decision

**Issue criterion violated:** The latest issue review note explicitly requires removing compatibility mode and choosing one fail-closed migration. The required boundary contract also requires that a result without an authoritative completion boundary cannot be settled from session activity or legacy result semantics.

**Evidence:** `design.md:77` proposes a negotiated compatibility mode; `design.md:85` deploys the server in compatibility mode; `design.md:90` requires keeping the compatibility server during rollback. `tasks.json:92-98` makes compatibility mode and legacy-report handling part of T-005.

**Failure case:** The repository's current report path accepts a plain `WorkResult` in `WorkflowReportService` and sends it to the existing settlement path. A compatibility rollout leaves two settlement contracts active while the new receipt is supposedly authoritative. A legacy report can therefore continue through old success/failure behavior until an operator enables enforcement, which is exactly the cleanup-induced false failure the issue is fixing and explicitly rejects in its latest comment.

**Required plan disposition:** Remove compatibility mode from the design and T-005. Define a single fail-closed v1 transition: missing or invalid boundaries are rejected or recorded as recoverable unconfirmed, never settled through the legacy task result path. Define how already-started legacy journal entries are reconciled under that one contract.

### F-002: Dirty, unconfirmed, and committed-clean are not mapped to concrete Workflow states and actions

**Issue criterion violated:** The required contract demands separate dirty, unconfirmed, and committed-clean outcomes, with dirty and unconfirmed recoverable and unable to produce task, stage, or run business failures. The latest issue review note specifically requires mapping those outcomes to concrete TaskRun/stage/run states and allowed actions.

**Evidence:** The spec requires explicit recoverable outcomes at `spec.md:46-62`; `design.md:60-62` only says to add a dedicated settlement value analogous to Agent settlement; T-003 says only that the state is nonterminal at `tasks.json:53-55`. The current domain has only `TaskRunStatus.Pending/Running/Completed/Failed/Cancelled` (`TaskRun.cs:19`), `StageRunStatus.Pending/Running/AwaitingApproval/Completed/Failed` (`StageRun.cs:3`), and the existing status mapper only derives special wire states for Agent settlement (`WorkflowStatusMapper.cs:147-236`).

**Failure case:** An implementation can store a new recovery object while leaving the task as ordinary `Running`, allowing normal dispatch or stop/failure paths to act on it; or it can add a wire status without changing `NextWork`, stage advancement, reminders, cancellation, and settlement transitions. The plan does not say which task/stage/run values represent each outcome, whether the task remains assigned, which recovery operations are legal after lease expiry, or exactly how a later clean verification transitions to normal completion. That leaves the central non-failure guarantee dependent on an unstated state-machine choice.

**Required plan disposition:** Specify the concrete persisted and wire state for each outcome, the claim/advance/stop/cancel behavior while recoverable, the allowed lease and verification transitions, and the idempotent transition from dirty or unconfirmed to completed or explicit recovery. Keep conclusive Action failure on its existing failed path.

### F-003: Later verification is described both as a new receipt and as separate mutable evidence

**Issue criterion violated:** The immutable first `CommitReceipt` must remain unchanged while cleanup and later verification observations are recorded separately. Exact replay/conflict handling must reject a different receipt for the same execution identity, while the recovery scenario must still permit later clean verification.

**Evidence:** `design.md:31` and `tasks.json:14,23` say later verification observations are separate mutable recovery data. However, the recovery scenario at `spec.md:129-132` says recovery obtains a "new authoritative receipt" for the same task and workspace identity, while the exact-conflict requirement at `spec.md:134-151` says a different branch, HEAD, tree, or status for that identity is a conflicting receipt and must be rejected. T-004 only calls this a "later-authoritative-verification recovery" (`tasks.json:70-78`) and defines no separate wire operation or value.

**Failure case:** After an initial dirty receipt, cleanup produces a clean workspace. If the clean observation is submitted as another `CommitReceipt`, exact conflict handling rejects the legitimate recovery. If the original receipt is overwritten, the immutable-boundary requirement is violated and the receipt no longer records the action completion boundary.

**Required plan disposition:** Name and define a separate `WorkspaceVerification` observation/operation, including its identity and generation checks, idempotency key, relationship to the immutable boundary fingerprint, and the precise settlement transition it can cause. Rewrite the recovery scenario so it never calls that later observation a replacement receipt.

### F-004: No authorized recovery path exists for legal uncommitted task-source changes

**Issue criterion violated:** Cleanup may remove only explicitly scoped generated artifacts and must preserve task source, task commits, declared outputs, recorded artifacts, and unrelated changes. The latest issue review note additionally requires an explicit authorized path for legal task-source changes that remain uncommitted.

**Evidence:** `design.md:49` says an unscoped task change remains dirty and recovery may only inspect it, obtain verification, or allocate a fresh generation. The spec repeats that cleanup leaves task-source changes untouched at `spec.md:102-106`. T-004 explicitly refuses task-source deletion at `tasks.json:74-77`, but defines no operation that can authorize, preserve/adopt, or otherwise complete such a source change.

**Failure case:** A valid Action writes tracked implementation files but does not commit them. Scoped cleanup correctly refuses to delete them, so the outcome remains dirty. The plan then has no defined actor or operation that can make the workspace eligible for a later clean verification without rerunning the Action or violating the no-source-deletion rule. A fresh workspace does not solve how the original valid implementation is settled or preserved as the task result.

**Required plan disposition:** Define the authorized source-change recovery path and its owner/fence. State whether recovery may create a task commit, whether an operator or a new Action may do so, how source paths are distinguished from generated cleanup paths, and how the resulting verification and settlement remain receipt-immutable and idempotent. The path must preserve source and declared output files on every rejection.

### F-005: The proposed boundary insertion does not cover all Workflow task failure exits

**Issue criterion violated:** The durable-boundary requirement says every Workflow task attempt produces an immutable `ActionCompletion` and matching `CommitReceipt`; T-001 also requires successful and failed Workflow attempts to be representable. A failure report must not bypass the boundary before settlement.

**Evidence:** T-002 places the common builder "after Action result normalization and artifact capture" (`tasks.json:29`) and its tests mention only conclusive Action failure (`tasks.json:35`). In the current runner, Workflow task execution returns before that point for workspace preparation failure (`executor.ts:93`), unknown or removed Action (`executor.ts:154-157`), unresolved or invalid input (`executor.ts:171-180`), start branch violation (`executor.ts:187-189`), and normalized Action failure (`executor.ts:227-231`). The outer catch also returns a plain failure (`executor.ts:282`). Artifact capture and `enforceCleanWorktree` begin only at `executor.ts:237-253`.

**Failure case:** If the implementation follows the described post-normalization/post-artifact placement, any of those exits can still become a plain failed `WorkItemResult` with no ActionCompletion/CommitReceipt, and the server can settle it through the existing path. That violates the first requirement even if the valid-success path is durable.

**Required plan disposition:** State which failure exits are included and route every in-scope Workflow task terminal result through the boundary builder, including an explicit representation for failures before an Action result exists. Add deterministic tests for workspace setup, input/dispatch validation, branch-probe failure, Action throw, and report/serialization failure, or explicitly narrow the requirement and issue scope (which would need issue approval).

## Dimension Verdicts

- **Issue grounding: checked, no issue.** The issue body and current comments were read before interpreting the artifacts. The review used the P1 goal, the immutable receipt/cleanup/recovery contract, the exact replay requirement, and the four explicit contract-gap notes.
- **Coverage: issues found.** The plan covers the named runner, server, cleanup, replay, and runtime paths, but it does not cover the concrete state machine, legal source-change recovery, fail-closed rollout, distinct later verification operation, or all failure exits. Findings F-001, F-002, F-003, F-004, and F-005 apply.
- **Correctness: issues found.** The initial receipt and later-clean-verification requirements conflict as written, and the compatibility rollout preserves the old false-settlement path. Without the missing state and recovery rules, the plan cannot guarantee that dirty or unconfirmed evidence remains recoverable.
- **Consistency with the current codebase: issues found.** The plan correctly identifies the current `enforceCleanWorktree` gate and existing `WorkResultJournal`, `WorkflowRunStore`, `TimeProvider`, and Agent settlement patterns. However, the current Workflow state enums, report admission path, artifact binding order, and executor early returns make the omitted state and boundary decisions behaviorally significant rather than documentation-only gaps.
- **Task breakdown: ordering checked, no dependency cycle; completeness has issues.** T-001 -> T-002/T-003 -> T-004 -> T-005 is acyclic and broadly ordered, and tests are attached to implementation tasks. T-005 nevertheless encodes the rejected migration, and no task owns the concrete state/action map, source-change authorization, or separate verification contract identified above.

## Observations

- The design leaves lease duration, cleanup budget, recovery deadline, status labels, and evidence retention as open questions (`design.md:92-96`). These are implementation/configuration decisions rather than must-fix problems as long as the final contract exposes a deadline or next action and bounds cleanup.
- The stable fingerprint is required, but the artifacts do not specify canonical serialization or equality rules for JSON output, artifact references, path ordering, and diagnostic truncation. This should be pinned before implementation to avoid equivalent replays becoming conflicts; the exact-replay acceptance criteria at least make the required behavior testable.
- T-002 through T-005 span runner, server, API/status, workspace, migration, and end-to-end behavior. The dependency ordering is sound, but each task will need implementation-level file ownership and an explicit cross-process test seam to remain verifiable.
- The plan mentions Web and CLI status consumers in `proposal.md:18`, but the task list names server status/API projections and rollout documentation without a client task. The issue acceptance text does not independently require a particular UI label, so this remains an integration follow-up rather than a must-fix for this review.

<promise>FAIL</promise>