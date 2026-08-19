# Review

## Verdict

FAIL. The durable deadline transition and post-release active-work projection are implemented, but must-fix acceptance gaps remain in late-result fencing and cleanup retry behavior.

## Must-Fix Findings

### 1. Workflow Agent terminal reports still bypass the complete execution-identity fence

Locations: `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:1005-1018`, `packages/server/src/Mohist.Server/Runner/Services/WorkflowReportService.cs:38-65`, `packages/runner/src/server/connection.ts:151-177`, `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:596-608`, and `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Work.cs:123-169`.

The T-004 acceptance criterion and capability requirement at `openspec/changes/issue-627/tasks.json:77` and `openspec/changes/issue-627/specs/workflow-agent-settlement-liveness/spec.md:123-130` require nullable `AgentSessionId`, `AgentTurnId`, `Runtime`, and `RuntimeSessionId` fields on the existing `/report` envelope, require all four for a Workflow Agent terminal result, and require incomplete bindings to be acknowledged stale. `RunnerReportRequest` has none of those fields, and the runner's normal `report()` request sends none. The server consequently routes the result through `WorkflowReportService` to `ReceiveTaskReportAsync`, where `FindReportableWork` accepts a blocked Agent task using only the TaskRun/work/Runner tuple. The assignment is already null, but the fallback branch deliberately makes that blocked task reportable.

This means a report with a matching reusable tuple can settle the released task without proving the Agent session, turn, runtime, or runtime-session identity. It also means `host-execution.ts:261-264` can fall back to the tuple-only `/report` path when no runtime binding is present, despite the requirement that a missing binding be stale rather than guessed. The separate `/recovery-receipt` path has a full binding check, but leaving the ordinary Workflow Agent report path open defeats that fence. Route Workflow Agent terminal reports through the full fence, carry and persist the four fields from the same executed turn, make incomplete or mismatched Agent receipts stale and side-effect free, and preserve the existing non-Agent report path.

### 2. An overdue authoritative receipt can win before the durable release boundary

Location: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:351-409` and `:479-509`.

`ReceiveRecoveryReceiptAsync` validates and applies a terminal receipt without calling `ReconcileAgentResultSettlementIfDueAsync` first. The only settlement reconciliation occurs after the task report is applied at `:578-582`. Therefore, if fake time is at or past the persisted deadline while the settlement is still `Unknown` because the reminder has not run, a matching terminal receipt goes directly through `FindReportableWork`, completes or fails the task, and skips the required `Unknown -> Blocked` release boundary and its blocked events. This violates the T-004 criterion at `openspec/changes/issue-627/tasks.json:80` and the requirement that the deadline be the arbitration boundary, including the late-result scenarios in `spec.md:123-142`.

Reconcile a due settlement before receipt lookup and terminal-result application. The late receipt may then settle the original blocked attempt through the full identity fence, but it must not bypass the persisted boundary or restore any released ownership.

### 3. Dispatch snapshot cleanup failures are swallowed and can remove the retry reminder

Locations: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:930-941` and `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.AgentResultSettlement.cs:138-151`.

The blocked cleanup treats `DeleteAgentResultSettlementSnapshotAsync` as a boolean success/failure operation, but its implementation calls `DeleteSnapshotBestEffortAsync`, which catches every exception and returns normally. A real snapshot-delete exception therefore makes `snapshotReleased` true. If stage-lock cleanup succeeds, `ReleaseBlockedAgentResultSettlementAsync` removes the settlement reminder, so there is no reminder replay for the unfinished snapshot cleanup; the row is left for an unrelated startup sweep. This violates the cleanup requirement and explicit snapshot-failure scenario at `openspec/changes/issue-627/specs/workflow-agent-settlement-liveness/spec.md:51-57`, as well as T-001's independently retryable cleanup criterion.

The failure-injection hook at `packages/server/tests/Mohist.Server.SpecTests/Specs/Workflow/Grain/WorkflowGrainStateSaveFailureSpecs.cs:508-517` returns `Task.CompletedTask` on its simulated failure, so the deadline cleanup tests do not exercise the swallowed-exception path. Make the settlement cleanup deletion report failure to the reconciler, retain the reminder until deletion succeeds, and add a deadline snapshot-failure test that proves reminder replay or activation retries the deletion without duplicate events or renewed ownership.

## Dimension Checks

- Issue basis: checked, no issue. I read the canonical issue body and all current comments with `mo issue view 627 --project proj_f6c141d63b6243bfbb481737b2243b87 --json body,comments,...` before reviewing the diff. The review basis is the durable unknown deadline, release of active work and Runner capacity, preservation of identity and disposition, full-fence late arbitration, and deterministic failure-injection coverage.
- Coverage: FAIL because the changed tests cover the successful release path, post-release capacity, tuple-only late reports, and existing receipt behavior, but do not cover incomplete Agent bindings on `/report`, an overdue receipt arriving before reminder delivery, or a real snapshot-delete exception during deadline cleanup.
- Correctness: FAIL for the three findings above. The Unknown-to-Blocked save, assignment clearing, blocked projections, active-work queries, and slot release otherwise match the acceptance scenarios.
- Surrounding-code consistency: checked, no additional issue. The implementation follows the existing aggregate-save, materialized active-work projection, and Orleans reminder patterns. The late-result ingress is internally inconsistent because the full-fence receipt protocol and the ordinary tuple-only report protocol both remain usable for Workflow Agent work.
- Tests: FAIL for acceptance completeness. Focused execution passed `WorkflowGrainStateSaveFailureSpecs` (22), `DispatchServiceReconciliationSpecs` (25), `AgentResultSettlementSpecs` (17), and `RecoveryReceiptSpecs` (5). Those tests do not expose the three missing failure cases; the full verification run was not rerun during this review, although `progress.txt` records a prior clean committed verification.

## Observations

- The unresolved recovery probe in `packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:401-441` returns `ReserveSlot: false`, but pre-deadline Unknown rows still appear in `FindRunningAssignedToAsync` and `CountRunningAssignedToAsync`, and `RunnerGrain.TryClaimWorkflowAsync` rechecks that active set. The focused capacity tests therefore show that an Unknown attempt remains a capacity reservation while a recovery probe can still be delivered; this is not a must-fix for the stated release boundary.
- `openspec/changes/issue-627/design.md` retains operational open questions about public reason shape, workspace routing after assignment release, and cleanup sweep metrics. The implemented stable category plus persisted reason/deadline is sufficient for this issue, so these remain observations.

<promise>FAIL</promise>
