# Issue 567 Review

## Verdict

FAIL. This is a re-review. The prior review's two remaining must-fix findings are still present in the current implementation. The earlier findings that were reported as fixed remain fixed on their covered paths, but these receipt-retry failure windows still make recovery reporting and terminal receipt handling incomplete.

## Must-Fix Findings

### MF-1: Workflow terminal-result replay can acknowledge a receipt while leaving the update operation unresolved

**Violates:** `runtime-agent-recovery-receipt`, "The Runner retries the exact receipt until the Server acknowledges it" and "Server arbitration applies receipts at most once"; `runner-update-recovery-reporting`, "The update waits boundedly for per-work recovery acknowledgement" and "Success is never claimed while affected work is unresolved".

`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:344-366` repairs the operation ledger on a duplicate only when the replayed payload is `update-interrupted`; every duplicate `terminal-result` returns the stored acknowledgement immediately. The first terminal receipt commits the task state and `AppliedRecoveryReceipts` at `:531`, then performs the cross-grain operation update at `:536-550`: `SettleUpdateOperationWorkAsync` for an original fenced result, or `MarkRecoverySettledAsync` for a replacement result.

If that operation-grain call fails after the Workflow commit, the Runner retries the exact durable receipt. The retry finds the receipt ledger entry, returns `accepted`, and retires the Runner journal entry without retrying the missing operation update. An original fenced result can therefore remain `Marked`/unresolved and be reported unresolved despite its authoritative task result. A replacement result can remain at `receipt-acked` rather than `replacement-settled`, with the Workflow visibility stuck before `recovered`. Repair the corresponding operation ledger for terminal-result duplicates before returning the stored acknowledgement, or introduce an equivalent durable outbox boundary. Add deterministic failure-injection coverage for both original fenced and replacement terminal receipts failing between the Workflow commit and the operation-grain write.

### MF-2: AgentJob terminal-result failure can turn a valid retry into a terminal stale acknowledgement

**Violates:** `runtime-agent-recovery-receipt`, "A matching terminal-result receipt applies through the authoritative settlement exactly once" and "The Runner retries the exact receipt until the Server acknowledges it"; `runner-update-recovery-reporting`, "Success is never claimed while affected work is unresolved".

In `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:102-142`, terminal-result handling calls `ApplyRecoveryTerminalResultAsync` before adding `State.AppliedRecoveryReceipts` and persisting that receipt ledger at `:127-131`. For an update-interrupted job, `ApplyRecoveryTerminalResultAsync` durably persists the terminal/recovered job state and then calls the cross-grain `MarkRecoverySettledAsync` at `:418-460`.

If that operation-grain write fails after the job state is persisted, the applied-receipt entry was not persisted. The exact receipt retry reaches the `IsTerminal` guard at `:85` and returns `stale` instead of retrying the operation settlement. The Runner treats `stale` as a terminal acknowledgement and retires the receipt, so the update operation can remain unresolved (or fail to reach `replacement-settled`) even though the terminal result was already applied. Persist a repairable acknowledgement before exposing terminal state, or make the terminal retry path recognize this state and repair the operation ledger before returning `stale`. Add deterministic failure-injection coverage for the operation write after terminal-state persistence and verify that exact replay settles the operation.

## Re-review Disposition Checks

The following earlier findings were checked against the current tree and remain properly fixed on their intended paths: Workflow Pi runtime-turn binding; terminal-result versus update-fence race arbitration; cross-language error-result fingerprints; chained update fencing; AgentJob duplicate acknowledgement repair when the applied-receipt ledger already exists; update-stop transport-error classification; Runner shutdown/restart replay coverage; Workflow replacement-result recovered projection on the nominal path; bounded shutdown receipt delivery; and premature CLI complete-success output. MF-2 above is a distinct earlier crash window: it occurs before the AgentJob applied-receipt ledger exists, so the existing duplicate-repair branch cannot run.

The prior non-blocking observations remain observations: update-operation history has no visible retention policy; the shutdown handoff races without aborting the underlying request; the CLI validates the count but not equality of affected work-id sets; and `receipt-acked` is treated as recovered before replacement settlement in the current reporting model. None changes the verdict beyond the two must-fix findings above.

## Dimension Checks

- **Issue criteria:** FAIL. The issue's required active-work interruption, receipt retry, replacement recovery, and honest per-work outcome are implemented on nominal paths, but a valid receipt can be terminally acknowledged while durable recovery status is not completed.
- **Coverage:** FAIL. The changed tests cover nominal terminal settlement, replacement settlement, duplicate delivery, and bounded shutdown, but no deterministic test covers either owner-commit/cross-grain-write failure window.
- **Correctness:** FAIL. Both defects are reachable transient failures at the owner-grain to update-operation durability boundary; they do not require malformed input or speculative runtime behavior.
- **Consistency:** checked, no additional issue. The implementation follows the existing owner-grain, receipt-ledger, and update-operation patterns; the gap is incomplete retry ordering at the cross-grain boundary.
- **Tests:** checked. The focused current Workflow and AgentJob recovery suites pass `20/20`; that nominal result does not cover the two missing failure-injection cases. The prior broader verification dispositions were rechecked and no regression was found outside these findings.

<promise>FAIL</promise>
