# Issue 567 Review

## Verdict

FAIL. This re-review confirms that the two prior receipt-retry findings are fixed, but finds one remaining must-fix durability gap in interruption visibility.

## Must-Fix Findings

### MF-1: Session interruption visibility is not repaired after the owner grain commits

**Violates:** `agent-work-interruption-visibility`, "The AgentSession, AgentTurn, and workflow-task read models and API DTOs SHALL expose the update-caused interruption lifecycle for every work named by a durable update operation" and its `interrupting` / `interrupted`, `recovering`, and `recovered` scenarios; this also violates the issue requirement that users see an explicit interruption/recovery state rather than an un-actionable error.

The Workflow and AgentJob owner state is committed before the separate AgentSession projection is written. In `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:47-75`, `MarkUpdateInterruptedAsync` commits the Workflow interruption events and then calls `ApplySessionInterruptionAsync`. If that Session-grain write fails, the route fails after the Workflow is already durably fenced. A retry calls `_run.MarkUpdateInterrupted`, which returns `Unchanged`; the retry only reaches `ReconcileAgentResultSettlementAsync` and never calls `ApplySessionInterruptionAsync` again. The route can then mark the operation work complete even though the Session and its AgentTurn have no `interrupting`/`interrupted` projection.

The same window exists for AgentJobs in `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:203-218` and `:264-268`: after the first Session call fails, a retry of an already `RecoverablyInterrupted` job only emits the pending AgentJob CloudEvent and returns; it does not retry the Session transition. The Session transition is a separate durable event path (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.Interruption.cs:8-19`), so the AgentJob event does not repair the missing Session state.

The gap also affects later lifecycle states. Workflow replacement allocation commits before `ApplySessionInterruptionAsync` at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:437-442`, while exact receipt replay at `:344-366` repairs only the update-operation ledger. AgentJob replacement and recovered projection similarly call the Session at `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:417` and `:450-460`, but the prior-receipt path at `:42-68` and the terminal-state path at `:83-94` do not replay that Session transition. A transient Session persistence failure after owner commit can therefore leave the Session stuck without `recovering` or `recovered`, while an exact receipt replay is acknowledged and the Runner retires its journal entry.

Make the Session transition a repairable durable obligation owned by the update/recovery protocol, or make every idempotent owner retry and exact receipt replay reapply the missing Session transition before returning its terminal acknowledgement. Add deterministic failure-injection tests for Session persistence failure at the initial fence, replacement allocation, and replacement settlement, asserting that a later retry produces the complete Session/AgentTurn lifecycle without duplicate transitions.

## Re-review Disposition Checks

The prior Workflow terminal-result replay finding is fixed: the duplicate-receipt branch now calls `RepairTerminalResultOperationAsync` for terminal receipts before returning the stored acknowledgement, covering both original fenced results and replacement results (`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:344-366`, `:740-797`).

The prior AgentJob terminal-result replay finding is fixed: the duplicate accepted-receipt branch repairs the operation ledger, and the terminal-state path also repairs it before returning a stale acknowledgement (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:42-94`, `:486-550`). The focused regression tests cover the original Workflow path, replacement Workflow path, and AgentJob replacement path. Those fixes do not repair the separate Session projection window above.

## Dimension Checks

- **Issue acceptance coverage:** FAIL. Immediate fencing, bounded interruption, durable receipt replay, replacement dispatch, reconnect deduplication, per-work reporting, and explicit no-receipt terminal handling are covered on their nominal paths. Session/turn visibility is not complete across an owner-commit/Session-write failure.
- **Correctness:** FAIL. The remaining defect is a reachable transient failure after durable owner state has been committed; the next idempotent request can acknowledge the operation without restoring the missing user-visible lifecycle state.
- **Consistency:** checked, no additional issue. The implementation follows the existing owner-grain and durable-receipt patterns, but the cross-grain Session projection lacks the same repair boundary.
- **Tests:** checked. Runner passed `1652/1652`, Web passed `4727/4727`, and CLI passed `1855/1855`. The full Server spec suite built and ran `3943/3944`; the single failure was the unrelated existing timing failure `AgentJobDispatchRouteSpecs.RunnerPollEndpoint_ForAgentJob_ExposesOwnerKindAndAgentJobId` at `packages/server/tests/Mohist.Server.SpecTests/Specs/Agent/Api/AgentJobRoutesSpecs.cs:494`. The issue-specific recovery classes passed, but no deterministic test covers the Session persistence failure window described above.

## Observations

1. `packages/cli/Mohist.Cli/Update/RunnerRefreshOutcome.cs:577` maps both `receipt-acked` and `replacement-settled` to CLI status `recovered`. This means a fully acknowledged interruption receipt can be reported as recovered before the replacement execution has settled. The state is still exposed as `receipt-acked`, and the current plan treats acknowledgement of replacement creation as sufficient for update success, so this is an observation rather than a verdict-changing finding.

2. `packages/runner/src/runtime/host.recovery.ts:155-159` races the pending-operation fetch against a timeout using a fresh `AbortController` that is never aborted when the race times out. The shutdown remains bounded, but a hanging fetch can remain in the process until its own transport timeout. This is a resource-management improvement outside the issue's must-fix behavior.

3. `RunnerUpdateOperationGrain` retains every historical operation in one persisted list without a visible retention or compaction policy. This can grow over time, but it does not make the current interruption/recovery behavior wrong.

<promise>FAIL</promise>
