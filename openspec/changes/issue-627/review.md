# Review

## Verdict

PASS. The three must-fix findings from the previous review are addressed in the current head, and the required identity, deadline ordering, cleanup retry, and non-regression paths are covered.

## Findings Resolution

### 1. Workflow Agent reports use the complete execution identity fence

`RunnerReportRequest` now carries nullable `AgentSessionId`, `AgentTurnId`, `Runtime`, and `RuntimeSessionId` fields. `RunnerRoutes` forwards all four fields to `WorkflowReportService`. For Workflow Agent tasks, the service rejects an incomplete binding as `stale` before it routes the result to the owning grain. A complete binding is passed to `ReceiveTaskReportAsync`, where the persisted task/work/Runner and Agent/runtime binding are checked together.

The Runner's normal `reportWork` transport forwards the four fields when the execution turn has a binding. `host-execution` has no tuple-only fallback for an Agent result: when the binding is absent, the report is sent without a complete identity and the server acknowledges it stale. Non-Agent task and check reports retain the existing tuple path.

Coverage includes incomplete and mismatched Agent report identity, the normal report wire envelope, and matching versus stale late results in `AgentResultSettlementIdentitySpecs`, `RecoveryReceiptSpecs`, `RunnerOutstandingWorkSpecs`, and Runner connection/report tests.

### 2. Due recovery receipts cross the durable deadline boundary first

`ReceiveRecoveryReceiptAsync` calls due-settlement reconciliation before task lookup, binding validation, or terminal result application. A receipt that arrives at or after the persisted deadline therefore observes the committed `Unknown -> Blocked` transition first. A matching receipt may still settle the original addressable attempt through the full binding fence, but it cannot restore assignment, active-work projection, dispatch state, stage locks, or Runner capacity.

`RecoveryReceipt_AtDeadlineCommitsBlockedBoundaryBeforeApplyingResult` verifies that the blocked event precedes the accepted late result. Mismatched, duplicate, superseded, and stale receipts remain side-effect free.

### 3. Blocked cleanup failures remain retryable

Snapshot deletion is now an awaited operation whose exception reaches `TryReleaseBlockedResourceAsync`. The blocked settlement reminder is removed only after both snapshot and stage-lock cleanup succeed. A snapshot-delete failure therefore leaves the durable release boundary intact and retains the reminder for replay or activation retry; cleanup does not reacquire ownership or duplicate blocked events.

`BlockedSettlement_SnapshotDeletionFailureRetainsReminderForReplay` and the settlement cleanup recovery specs exercise a real throwing failure injection and prove that a later replay deletes the snapshot without duplicate settlement events.

## Verification Evidence

- PR CI run `32281570222` passed: Detect Changed Paths, .NET spec tests, .NET build and test, and Node build and test.
- The current PR head is rebased onto `master` at `6dfddc5772064ae3a3ec5990a5580f7cd46acc7e` and is mergeable.
- Local post-rebase `RunnerPollRecoveryStateApiSpecs`: 6/6 passed.
- Local post-fix `DirectApiFollowupSpecs`: 9/9 passed.
- Prior focused settlement, state-save, recovery, and Runner identity suites passed 54/54 and 44/44 respectively; the full CI Spec and Node gates passed again after the rebase and projection-drain repair.
- The local server and Runner were deployed from the preceding implementation head and verified healthy before the final rebase; the final post-merge deployment remains a separate verification step.

No must-fix findings remain.

<promise>PASS</promise>
