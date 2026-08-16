# Issue 567 Review

## Verdict

FAIL. This is a re-review. The three must-fix findings from the previous review are fixed on their normal paths, but terminal-result replay still has two reachable crash/retry gaps in the current implementation.

## Must-Fix Findings

### MF-1: Workflow terminal-receipt replay does not repair the update-operation ledger

**Violates:** `runtime-agent-recovery-receipt`, "The Runner retries the exact receipt until the Server acknowledges it" and "Server arbitration applies receipts at most once"; `runner-update-recovery-reporting`, "The update waits boundedly for per-work recovery acknowledgement".

In `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:342-366`, the duplicate-receipt branch repairs the update operation only when the replayed payload is `update-interrupted`. It returns the stored acknowledgement without repair for every `terminal-result`. That leaves a crash window in the terminal path: the Workflow durable receipt ledger and task state are committed at `:527-531`, but the cross-grain operation update happens afterward at `:536-552`.

If that operation-grain call fails after the Workflow commit, the Runner retries the exact terminal receipt. The retry finds the stored receipt, returns `accepted`, and retires the Runner journal entry, but never calls `MarkReceiptAckedAsync` for an original fenced result or `MarkRecoverySettledAsync` for a replacement result. An original result therefore remains `Marked`/unresolved in the update operation and causes the CLI recovery wait to report unresolved despite an authoritative result. A replacement result can leave the operation at `receipt-acked` instead of `replacement-settled`, even though the replacement task is already settled. Repair the appropriate operation ledger for terminal-result duplicates (or use a durable outbox/transaction boundary) before returning the stored acknowledgement. Add deterministic failure-injection coverage for both an original fenced terminal receipt and a replacement terminal receipt failing between the Workflow commit and the operation-grain update.

### MF-2: AgentJob terminal-receipt failure can become a terminal stale acknowledgement without settling the update

**Violates:** `runtime-agent-recovery-receipt`, "A matching terminal-result receipt applies through the authoritative settlement exactly once"; `runner-update-recovery-reporting`, "Success is never claimed while affected work is unresolved".

In `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:95-139`, terminal-result handling calls `ApplyRecoveryTerminalResultAsync` before it appends `State.AppliedRecoveryReceipts`. That helper persists the terminal/recovered AgentJob state and then calls the cross-grain `MarkRecoverySettledAsync` at `:459-475`. If that operation-grain call fails, the job is already durably terminal but no applied-receipt record exists. The next exact delivery reaches the `IsTerminal` check at `:75-81`, returns `stale`, and never retries the operation settlement. The Runner treats that stale acknowledgement as terminal and retires the receipt, leaving the affected update operation unresolved and making a valid returned result appear unresolved to the CLI. Record and replay a durable acknowledgement with repairable settlement state, or otherwise make the terminal retry path repair `MarkRecoverySettledAsync` before returning `stale`. Add a deterministic test that fails the operation-grain write after terminal state persistence and verifies exact replay settles the operation.

These are pre-existing gaps missed by the earlier review rounds: those rounds verified nominal duplicate-receipt repair and nominal terminal settlement, but did not exercise failure between owner-grain persistence and the cross-grain operation update. They are reachable transient-failure cases in the receipt protocol, not speculative malformed-input cases.

## Re-review Disposition Checks

- Previous MF-1, Workflow Pi binding: fixed. `packages/runner/src/runtime/executor-capabilities.ts` passes the runtime-turn registry and canonical work key into the Pi Workflow path; host-level tests cover the resulting binding.
- Previous MF-2, terminal-result/update-fence race: fixed. `WorkflowGrain.Reports.cs` accepts a matching terminal result after the update fence without allocating a replacement and settles the operation on the normal path.
- Previous MF-3, cross-language error fingerprint: fixed. `RuntimeRecoveryReceipt.cs` hashes the explicit shared result payload, excluding the Server-only `ErrorCode`; AgentJob HTTP coverage exercises an error result.
- Previous MF-4, chained update fencing: fixed. `RunnerRoutes.UpdateRecovery.cs` distinguishes a retry on the same connection from a fresh connection, and the chained-operation spec covers a new fence while an older one is unresolved.
- Previous MF-5, AgentJob duplicate acknowledgement repair: fixed for receipts whose applied-receipt ledger was already persisted; the current MF-2 is the earlier terminal-state/operation-write failure window that this repair does not cover.
- Previous MF-6, raw update-stop transport errors: fixed at the update boundary and user-facing session/web surfaces; the host test verifies sanitized update-scoped reporting.
- Previous MF-7, Runner shutdown/replay coverage: fixed for the covered shutdown paths. The Runner host tests now cover handoff, confirmed stop, interrupted journal replay, terminal replay, and duplicate delivery; the missing cases are the Server crash/retry windows reported above.
- Current MF-1, replacement Workflow terminal receipt: fixed on the normal path. The replacement receipt test asserts `recovered` visibility and `replacement-settled` operation status.
- Current MF-2, hanging receipt delivery: fixed on the bounded shutdown path. The host test proves shutdown returns, the receipt remains in the journal, and retry remains scheduled when delivery hangs.
- Current MF-3, premature CLI success: fixed. `UpdateOperations.cs` emits complete success only after runtime verification and bounded recovery reporting; the unresolved-output regression test passes.

## Dimension Checks

- **Acceptance coverage:** FAIL. The durable fence, receipt protocol, replacement allocation, visibility, bounded shutdown, reconnect behavior, and CLI reporting are present on the normal paths, but MF-1 and MF-2 leave valid terminal receipts able to be acknowledged while durable recovery reporting remains unresolved.
- **Correctness:** FAIL. Both findings are reachable when a cross-grain operation write fails after the owner grain has committed; the next exact receipt delivery loses the required repair opportunity.
- **Consistency with the surrounding codebase:** checked, no additional issue. The implementation follows the existing journal, owner-grain, and operation-ledger patterns; the defects are incomplete failure ordering at those boundaries.
- **Tests:** FAIL for the change as a whole because no deterministic test covers either operation-write failure window. Current focused verification passed: Runner `1652/1652`, CLI `1855/1855`, Web `4727/4727`, Server Unit `2667/2667`, and the issue-specific Workflow recovery class `3941/3941`. The full Server SpecTests run was `3940/3941` because of the unrelated `AgentSessionLaunchRuntimeResolutionSpecs.Launch_WithoutRuntimeOverrideOrConfig_DefaultsToOpenCode` failure; that class passes in isolation.

## Observations

- `RunnerUpdateOperationGrain` retains historical operations in one persisted list without a visible retention policy.
- `RunnerHostRecovery.pendingUpdateOperationForShutdown` uses a bounded race but does not abort the underlying handoff request when the race expires; the caller remains bounded, so this is a resource-lifetime concern rather than another update-correctness blocker.
- `RunnerRefreshOutcome` validates that `affectedWorks` has the same count as `interruptedWorkIds` but does not validate that the two work-id sets match. The Server currently constructs them together, so this remains an input-validation observation.
- The CLI treats `receipt-acked` as recovered before a replacement has necessarily settled; this matches the current task/spec acknowledgement model, while `replacement-settled` remains the stronger durable phase.

<promise>FAIL</promise>
