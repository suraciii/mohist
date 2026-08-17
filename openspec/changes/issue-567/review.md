# Issue 567 Review

## Verdict

FAIL. The prior must-fix Session visibility finding remains unresolved on the current branch.

## Must-Fix Findings

### MF-1: Session interruption visibility is still not repairable after the owner grain commits

**Violates:** `agent-work-interruption-visibility`, "The AgentSession, AgentTurn, and workflow-task read models and API DTOs SHALL expose the update-caused interruption lifecycle for every work named by a durable update operation," including the `interrupting` / `interrupted`, `recovering`, and `recovered` scenarios. It also violates the issue requirement that affected work remains explicitly visible with an actionable recovery path.

The current implementation persists the owner grain before applying the corresponding Session transition, but does not retain a durable obligation to retry that cross-grain write:

- For Workflow work, `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:23-78` commits the `interrupting` and `interrupted` Workflow events at `:64-69`, then calls `ApplySessionInterruptionAsync` at `:71-74`. If the Session grain persistence fails, the Workflow fence is already durable. A retry of `MarkUpdateInterruptedAsync` returns an unchanged owner update and skips that block, so it only reconciles the settlement and never repairs the missing Session/AgentTurn projection.
- The same initial-fence window exists for AgentJobs at `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:196-270`: `PersistAsync()` at `:263` precedes the two Session calls at `:264-267`, while the already-`RecoverablyInterrupted` retry branch at `:203-219` only retries the pending CloudEvent and returns. A transient Session failure therefore leaves the durable AgentJob interruption without Session visibility, and the next idempotent marking call can succeed without repairing it.
- The later lifecycle transitions have the same ordering gap. Workflow replacement allocation commits before `ApplySessionInterruptionAsync` at `:437-445`; exact receipt replay at `:344-367` repairs only the update-operation ledger. Workflow terminal-result settlement commits before the recovered Session write at `:530-547`, while duplicate terminal receipts call `RepairTerminalResultOperationAsync` but do not reapply the Session transition. AgentJob recovery and recovered projection similarly write owner state before Session at `:320-407` and `:410-450`; its duplicate receipt paths at `:42-73` and `:80-94` repair operation status only.

This is a reachable partial-failure case, not a nominal-path concern: users can receive a durable update-operation acknowledgement and owner state while the Session and AgentTurn remain without the required lifecycle state. Make Session delivery a durable, retryable obligation owned by the update/recovery protocol, or ensure every idempotent owner retry and exact receipt replay reapplies the missing transition before returning a terminal acknowledgement. Add deterministic failure-injection tests for Session persistence failure at initial fencing, replacement allocation, and replacement settlement, verifying that retry restores the complete Session/AgentTurn lifecycle without duplicate transitions.

## Re-review Disposition Checks

- **Previous MF-1:** still unaddressed on this HEAD. The separate Session-delivery repair commit is not an ancestor of the reviewed branch, and no pending Session-delivery state or retry path exists in the current source.
- **Prior Workflow and AgentJob terminal-result receipt-replay findings:** remain repaired on their nominal operation-ledger paths. Duplicate receipts call the corresponding operation-repair methods before returning their stored acknowledgement; this does not repair the separate Session projection failure above.
- **Prior handoff cancellation observation:** fixed in `packages/runner/src/runtime/host.recovery.ts:144-177`; each pending-operation request now has its own `AbortController` and is aborted in `finally` when the bounded handoff completes or expires.
- **Prior CLI `receipt-acked` observation:** remains a non-blocking observation. The CLI maps both `receipt-acked` and `replacement-settled` to the user-facing recovered status while retaining the underlying state in the report; the current plan explicitly treats receipt acknowledgement as sufficient for the bounded update outcome.
- **Prior update-operation retention observation:** remains non-blocking. `RunnerUpdateOperationGrain` still stores historical operations in one persisted list without a visible compaction policy, but this does not make the current recovery behavior wrong.

## Dimension Checks

- **Acceptance coverage:** FAIL. Immediate fencing, bounded shutdown, receipt replay, replacement dispatch, reconnect deduplication, per-work reporting, and explicit no-receipt handling remain covered on nominal paths; Session/AgentTurn visibility is incomplete across an owner-commit/Session-write failure.
- **Correctness:** FAIL. A transient cross-grain persistence failure can leave the durable owner state and update-operation outcome ahead of the user-visible Session lifecycle, and the next idempotent request can acknowledge without repairing it.
- **Regression check:** checked, no additional must-fix regression found in the post-review hardening. The exact Workflow inventory matching, bounded handoff cancellation, runtime stop-confirmation checks, recovery-generation propagation, and bounded CLI wait remain covered by the current implementation and tests.
- **Consistency:** checked, no additional issue. The owner-grain and receipt-ledger patterns are consistent; the missing repair boundary is the remaining cross-grain durability defect.
- **Tests:** checked. `npm run test:fast` passed its configured tracks: Workflow 178, CLI 1,856, Server Unit 2,674, Server Arch 68, Runner 1,662, Web 4,727, plus typechecks. The full Server SpecTests apphost passed 3,947/3,947. No current-branch test injects Session persistence failure after owner-grain commit at the three lifecycle points above, so green nominal suites do not close MF-1.

<promise>FAIL</promise>
