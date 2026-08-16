# Issue 567 Review

## Verdict

FAIL. This is a re-review of the previous FAIL. The seven previous must-fix findings were checked against the current implementation and are addressed, but the fixes leave two recovery-path correctness defects and one user-facing success-reporting defect.

## Must-Fix Findings

### MF-1: Replacement Workflow results delivered as receipts never become `recovered`

**Violates:** `agent-work-interruption-visibility`, "Replacement completion is visible as recovered"; it also leaves the recovery-status operation at the receipt-acknowledged phase instead of recording replacement settlement.

`packages/runner/src/runtime/host.ts:792` creates a `terminal-result` receipt for every bound result, including the new work id and generation created by a Workflow recovery attempt. In `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:465`, the receipt path only sets interruption visibility to `recovered` when `wasUpdateInterrupted` is true. That condition is true for the original fenced attempt, but false for the replacement, whose settlement is `AwaitingResult` and whose `TaskRun.Interruption` is `recovering`.

As a result, a replacement terminal receipt can settle the task without advancing its interruption transition to `recovered`. The same branch calls `MarkReceiptAckedAsync` using the replacement work id, but the durable update operation inventories the original work id, so it cannot record `ReplacementSettled` either. The normal `ReceiveTaskReportAsync` path has the needed `recovering` handling at `WorkflowGrain.Reports.cs:557`, but the receipt path does not. A real Runner uses the receipt path, so the issue's normal reconnect/recovery flow leaves Workflow read models stuck at `recovering` and can misrepresent the durable recovery phase. Handle a replacement terminal receipt by applying the recovered transition to the replacement task/session and calling `MarkRecoverySettledAsync` for the original operation work. Add a deterministic test that creates a replacement, binds it, delivers its terminal receipt, and asserts recovered visibility plus `replacement-settled` operation status.

### MF-2: Interruption receipt delivery can defeat the bounded Runner shutdown

**Violates:** `runner-update-work-interruption`, "The update stops the old Runner promptly without waiting for Agent turns to finish" and "The shutdown handoff is bounded"; the issue requires the old Runner to be terminated and restarted promptly even when the Server is unavailable.

`packages/runner/src/runtime/host.recovery.ts:69` writes the interruption receipt and then awaits `reportOnce`. `reportOnce` sends the receipt at `host.recovery.ts:249` with a fresh `AbortController` that has no timeout. The bounded `Promise.allSettled` wait in `shutdownInFlight` only covers `entry.done` before this call; it does not bound the subsequent receipt HTTP request.

If `/recovery-receipt` hangs while the Server is restarting or unreachable, `persistInterrupted` never returns, `runWorkerPool` never completes its shutdown, and `RunnerHost.run` cannot reach `shutdownSharedConnection` or return for the service restart. The receipt is already durable and is designed to retry after restart, so the first delivery attempt must be bounded or scheduled without holding the shutdown path. Add a fake hanging delivery test that proves shutdown returns within the configured stop budget while the exact receipt remains in the journal for later retry.

### MF-3: `mo update runner` emits a complete-success message before recovery is known

**Violates:** `runner-update-recovery-reporting`, "Success is never claimed while affected work is unresolved" and the issue requirement that the CLI must not claim a complete success when recovery cannot be confirmed.

The non-managed `mo update runner` path writes `Runner updated successfully.` at `packages/cli/Mohist.Cli/Update/UpdateOperations.cs:174`, before service restart, runner identity verification, and the bounded recovery wait at `UpdateOperations.cs:193`. When an affected work remains unresolved, the method correctly returns exit code 1 and later writes an unresolved error, but the stdout success claim has already been emitted. A user therefore sees a success claim and a failure for the same update, and the emitted outcome is not honest about the unresolved work.

Move the complete-success message until after identity and recovery checks, or make the pre-recovery message explicitly describe only a completed build/restart stage. Add an unresolved-update assertion that the output does not contain a complete-success claim.

## Re-review Disposition Checks

- **MF-1 from the previous review, Workflow Pi binding:** fixed. `packages/runner/src/runtime/executor-capabilities.ts:562` now passes the runtime-turn registry and canonical work key to the Pi Workflow path. The host-level Pi-shaped interruption test and the full Runner host replay tests cover the resulting receipt lifecycle.
- **MF-2 from the previous review, terminal-result/update-fence race:** fixed. `WorkflowGrain.Reports.cs:465` validates the frozen binding and applies a matching result without allocating a replacement; `AgentResultSettlementSpecs.cs:439` covers the race, including an error result.
- **MF-3 from the previous review, error-result fingerprint mismatch:** fixed. `RuntimeRecoveryReceipt.cs:167` hashes the explicit seven-field cross-language payload without the Server-only `ErrorCode`, and `AgentJobRecoveryReceiptSpecs.cs:115` covers a failed result through the HTTP receipt route.
- **MF-4 from the previous review, chained update fencing:** fixed. `RunnerRoutes.UpdateRecovery.cs:25` distinguishes retries on the same connection from a newly registered connection, and `RunnerConfigApiSpecs.cs:120` covers a fresh fence while an older operation remains unresolved.
- **MF-5 from the previous review, AgentJob duplicate acknowledgement repair:** fixed. `AgentJobGrain.Recovery.cs:42` repairs the operation ledger before returning an accepted duplicate acknowledgement; the AgentJob duplicate receipt coverage passes.
- **MF-6 from the previous review, raw update-stop transport errors:** fixed at the user-facing boundaries. `AgentWorkInterruptionContracts.cs:96`, the session/workflow DTO mappers, and `SessionDetailShell.tsx:228` sanitize or suppress the raw transport text; the Runner stop-failure test verifies the update-scoped message.
- **MF-7 from the previous review, missing Runner shutdown/replay coverage:** fixed in the covered portions. `host.recovery.test.ts` covers bounded handoff/stop behavior and `runner-host-reporting.spec.ts:249` covers interrupted journal replay and terminal-result replay without re-execution. The new MF-2 failure case, however, still needs a hanging-delivery test.

## Dimension Checks

- **Acceptance coverage:** FAIL. The durable fence, receipt protocol, replacement allocation, visibility, bounded CLI reporting, and duplicate-delivery paths are present, but MF-1 leaves a real replacement execution without the required recovered visibility, MF-2 can block the promised prompt restart, and MF-3 contradicts the CLI success rule.
- **Correctness:** FAIL. The failure cases above are reachable through the normal Runner receipt path and do not require malformed input or speculative timing.
- **Consistency with the surrounding codebase:** checked, no additional issue. The implementation follows the existing Orleans owner-grain, journal, DTO, and retry patterns; the problems are missing transitions/bounds at those boundaries rather than a convention mismatch.
- **Tests:** FAIL for the change as a whole. The issue-specific Runner host tests pass (`10/10` focused tests and `1651/1651` Runner tests), CLI tests pass (`1855/1855`), and `npm run test:fast` passes its configured suites. The current tests do not cover a replacement Workflow terminal receipt or a hung receipt delivery, and therefore would not catch MF-1 or MF-2.

## Observations

- `RunnerUpdateOperationGrain` retains all historical operations in one persisted list without a visible retention/GC policy. This is outside the issue's must-fix bar but will grow with every Runner update.
- `pendingUpdateOperationForShutdown` races the pending-operation fetch against its budget but does not abort the underlying HTTP request when the race expires. The bounded caller proceeds, so this is a resource-lifetime observation rather than another shutdown-correctness blocker.
- The full Server spec run during review reported `3939/3940`; the sole failure was the known unrelated `AgentSessionLaunchRuntimeResolutionSpecs.Launch_WithoutRuntimeOverrideOrConfig_DefaultsToOpenCode` default-runtime test. The configured `npm run test:fast` gate passed cleanly after the initial parallel build collision was removed.
- The previous review's six non-blocking observations remain non-blocking; no prior must-fix disposition was found to be invalid.

<promise>FAIL</promise>