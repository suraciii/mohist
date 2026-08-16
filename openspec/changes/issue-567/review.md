# Issue 567 Review

## Verdict

FAIL. This is the first review of the current implementation. I reread the live issue acceptance criteria before reviewing the diff, then checked the OpenSpec proposal, design, recovery matrix, task graph, and five capability specs against the product files relative to `origin/master`.

## Must-Fix Findings

### MF-1: Workflow Pi turns cannot produce an interruption receipt

**Violates:** the acceptance requirement that active Agent work is durably interrupted, restarts, reconnects, and resumes; specifically the runtime receipt requirement that a confirmed stop produces an `update-interrupted` receipt, and the requirement for deterministic active-work interruption tests.

`packages/runner/src/runtime/executor-capabilities.ts:534-556` calls `piAction` for the workflow `mohist/pi` capability but does not pass `deps.runtimeTurnRegistry` or the `workKey(work)` as `runtimeTurnKey`. `packages/runner/src/actions/pi.ts:231-237` therefore has no registry to update for this path. The OpenCode workflow path registers the physical binding at `executor-capabilities.ts:489-495`, and AgentJob paths do so separately, but workflow Pi work is absent.

During an update, `RunnerHostRecovery.persistInterrupted` needs that registry entry at `packages/runner/src/runtime/host.recovery.ts:71-82`; without it, `persistInterrupted` returns `false` and the Runner leaves only the `started` fence. The Server has already marked the task recoverably interrupted, so this Pi workflow task cannot get the replacement dispatch and eventually becomes unresolved at the deadline. Pass the registry and the correct work key through the Pi workflow path, and add a shutdown test using a Pi workflow turn that proves the receipt is persisted and delivered.

### MF-2: A Workflow terminal result that races the update fence is always rejected

**Violates:** the receipt acceptance criterion that a turn returning a result before or after the abort signal becomes a `terminal-result` receipt and is applied exactly once; also violates the requirement that the original work can resolve without duplicate execution.

`packages/runner/src/runtime/host.ts:773-798` deliberately sends a terminal receipt whenever shutdown produced a non-synthetic result, including a result returned after the cooperative abort. However, `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:417-418` unconditionally returns `stale/execution-stopped` whenever the settlement is `RecoverablyInterrupted`, before applying the terminal result. The receipt is therefore terminally acknowledged by the Runner but the Workflow task remains fenced, the update-operation entry is not acknowledged, and the CLI reports unresolved even though the runtime returned an authoritative result.

The Workflow arbitration must handle this race explicitly: validate the frozen binding, settle the returned result without allocating a replacement, and update the operation ledger consistently. Add a deterministic test where `MarkUpdateInterruptedAsync` occurs before a matching terminal receipt.

### MF-3: Error terminal receipts fail the cross-language fingerprint check

**Violates:** the receipt contract criterion that a matching `terminal-result` is accepted and applied exactly once, and the issue criterion that returned work is retained and recovered without loss.

The Runner canonical fingerprint in `packages/runner/src/runtime/recovery-receipt.ts:100-113` hashes `status`, message, output, exit code, artifacts, add-tasks, and `error`; it intentionally omits Runner-private fields. The Server fingerprint in `packages/server/src/Mohist.Server/Runner/Grains/RuntimeRecoveryReceipt.cs:176-180` hashes the normal JSON serialization of `WorkResult`. That type has a public computed `ErrorCode` property at `packages/server/src/Mohist.Server/Runner/Grains/IRunnerGrain.cs:322-329`, and `JSON.Options` does not ignore non-null read-only properties. Thus any result with an `error` hashes an additional `errorCode` field on the Server and is rejected as `result-fingerprint-mismatch`.

A failed Agent result, including the common runtime failure path, is retained and retried forever instead of being applied or acknowledged. Define one canonical cross-language payload shape and test it with both successful and error results through the HTTP receipt path.

### MF-4: A pending old operation prevents a later update from fencing replacement work

**Violates:** the criterion that each confirmed interrupt names every active Agent work at confirmation, and the recovery requirement that chained updates fence the current replacement identity.

`packages/server/src/Mohist.Server/Api/RunnerRoutes.UpdateRecovery.cs:23-43` treats any pending operation as an idempotent retry and never snapshots `runner.BeginUpdateInterruptAsync()` again. `GetPendingAsync` returns an operation solely based on `Status == Pending` at `packages/server/src/Mohist.Server/Runner/Grains/RunnerUpdateOperationGrain.cs:87-92`; a work with no receipt remains pending indefinitely because only receipt/settlement changes its operation work status.

Failure case: update A loses its old Runner before a receipt, leaving operation A pending; the replacement work for another affected item is now active; update B is requested. The route returns operation A, whose inventory contains only update A's original identities, so the active replacement is never marked or included in B's recovery report. The operation model needs a durable distinction between an interrupt retry before activation and a new update, or an explicit lifecycle that closes the old operation before a later update can create a fresh fence. Add a chained-update test covering an unresolved old work plus an active replacement.

### MF-5: AgentJob duplicate receipt acknowledgements do not repair a partially committed operation

**Violates:** the criterion that duplicate receipt delivery is idempotent while preserving the durable acknowledgement/reporting state, and the CLI criterion that acknowledged affected work is reported recovered.

In `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:42-58`, the prior-receipt branch returns the stored acknowledgement immediately. It only retries `TryAdmitAsync` for a replacement; it never calls `SettleUpdateOperationWorkAsync` for an `update-interrupted` receipt or `MarkReceiptAckedAsync` for a terminal receipt.

If the first request persists `State.AppliedRecoveryReceipts` at line 159 (or the terminal equivalent at lines 102-108) and then fails before the operation-grain update at line 160 (or lines 109-115), the next exact delivery returns `accepted` and retires the Runner journal, but the operation still reports that work as unresolved forever. Duplicate handling must repair the operation ledger before returning the stored acknowledgement, and a test must inject the failure between those two durable writes.

### MF-6: Update-caused runtime transport errors can still be shown as raw session failures

**Violates:** the visibility criterion that an update-caused stop failure carries update/work/state/recovery context and never presents raw text such as `session.abort fetch failed`.

`packages/runner/src/runtime/host.recovery.ts:190-218` only logs the stop failure; it does not create a durable `StopFailure` transition. The old runtime can then report its abort failure through the existing session event path. `packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs:738-755` always copies the transcript summary's `FailureReason`, even when an interruption transition exists, and `packages/web/src/pages/session/ui/SessionDetailShell.tsx:228-237` always renders `SessionErrorsEvidence` from that reason. Consequently a confirmed update can show the new interruption banner and the raw abort/fetch failure at the same time, rather than replacing the error with the actionable update recovery state.

Classify and persist the stop failure at the update boundary, and suppress or replace the raw failure evidence for work named by that operation. Add a session-level test with an abort transport failure and assert that the user-facing summary contains the operation/work recovery context but not the transport message.

### MF-7: The required shutdown/reconnect behavior is not covered by deterministic Runner tests

**Violates:** the issue's explicit acceptance requirement for deterministic coverage of active Agent work, interruption, restart, reconnect recovery, and duplicate delivery.

The changed Runner tests cover `WorkResultJournal` and receipt construction (`packages/runner/src/runtime/work-result-journal.test.ts` and `recovery-receipt.test.ts`) plus HTTP parsing (`packages/runner/tests/server-connection-report.spec.ts`), but there is no test for `RunnerHostRecovery` or the `RunnerHost` shutdown path. In particular, no test exercises the pending-operation handoff, runtime-confirmed stop mapping, receipt persistence before delivery, exact-receipt retry after restart, or the Pi workflow path identified in MF-1. The Server recovery specs construct receipts directly and therefore do not cover the Runner-to-Server sequence. Add deterministic host-level tests for the required handoff/stop/restart/reconnect sequence and duplicate delivery.

## Dimension Checks

- **Acceptance criteria:** FAIL. The durable fence, receipt, replacement, visibility, and CLI surfaces are present, but MF-1 through MF-6 leave required behavior incomplete or wrong.
- **Correctness:** FAIL. The failure cases above prevent recovery for workflow Pi turns, race-lost terminal results, error results, chained updates, and partially acknowledged AgentJobs.
- **Consistency with surrounding code:** Checked. The implementation generally follows the existing Orleans grain, runner journal, and DTO patterns; no additional must-fix consistency issue was found beyond the lifecycle gaps above.
- **Tests:** FAIL. Runner typecheck passed and the Runner suite passed (`153` files, `1647` tests), but the new shutdown/recovery behavior is not directly covered. The attempted Server command built successfully and ran `3936` specs; the requested filter was ignored by Microsoft Testing Platform, and two unrelated pre-existing Agent API tests failed, so that run is not a clean full-gate pass.

## Observations

- `RunnerUpdateOperationGrain` retains every historical operation in one growing list, with no defined retention/GC policy. This is outside the issue's must-fix bar but should be addressed before long-lived deployments accumulate update history.
- `RunnerHostRecovery.pendingUpdateOperationForShutdown` races a fetch against a timeout without aborting the fetch's `AbortController`; a slow request can continue after shutdown has moved on. The bounded shutdown behavior itself is preserved, so this is an observation.
- The OpenSpec self-review is a plan-artifact PASS, not a product review; it does not cover the implementation defects above.

<promise>FAIL</promise>
