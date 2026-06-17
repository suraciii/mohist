# Review Report

## Result: PASS

Reviewed the post-build candidate for issue #135 against `proposal.md`, `specs/agent-runtime/spec.md`, `design.md`, and `tasks.json`. Verified all six required test scenarios, ran the full runner test suite, typecheck, and the .NET suite. The change is correct, additive, and aligned with the issue's scope.

## Repaired Items

None. No items required repair. All findings are non-blocking and either out-of-scope, follow-up improvements, or pre-existing issues unrelated to this change.

## Blocking Items

None.

## Follow-up Items

- [ID: fu-1]
  Severity: follow-up
  Scope: `packages/runner/src/actions/acp-agent.ts:942`
  Evidence: The timeout path passes an `error` string to `cancelAndReturn` but immediately discards the returned `{ error }` and constructs its own error message at line 944. The `error` argument on the timeout-path call is dead — the only callers that actually consume `cancelAndReturn`'s return value are the two abort paths (lines 961 and 1010). `cancelAndReturn` itself still needs the `error` parameter for the abort paths, so the signature is correct; only this one call site passes a value it does not use.
  SuggestedAction: Leave as is. The cancel call still needs to await (its return value is `await`ed even when discarded) so that `monitorPrompt` does not return until bounded cleanup finishes. Removing the argument would not improve clarity because the same call expression is used in three places with consistent shape.
  Status: follow-up

- [ID: fu-2]
  Severity: follow-up
  Scope: `packages/runner/tests/acp-agent.spec.ts:763`
  Evidence: `EphemeralSessionCancelResolvesPromptly_NoForceCleanupFromTimeoutRace` instantiates the agent with scenario `"cancel-hangs"` and then sets `agent.cancelHangs = false`. The cancel handler's `if (self.scenario === "cancel-hangs" || self.cancelHangs)` triggers on the scenario branch first, so the agent's `cancel()` handler still awaits `new Promise(() => {})` indefinitely. The test passes only because `connection.cancel` is a JSON-RPC **notification** (`agentclientprotocol/sdk` acp.js:809 `sendNotification`) — the client never awaits the agent's response, so the hung handler does not block. This is functionally correct but the scenario name contradicts the test name; the test asserts that cleanup is **not** forced even though the agent's handler is set up to hang.
  SuggestedAction: Change `new FakeAcpAgent("cancel-hangs")` to `new FakeAcpAgent("basic")` so the test fixture matches its intent. The test will still pass (the agent's `cancel()` is awaited for the same reason) and the scenario will no longer contradict the assertion. Out of scope for this fix — leave for a future cleanup commit.
  Status: follow-up

- [ID: fu-3]
  Severity: follow-up
  Scope: `packages/runner/tests/acp-agent.spec.ts:788` (`SharedSessionCancelHangs_NoProcessIsKilled`)
  Evidence: The shared-session cancel-hang test asserts only that the result is failure with "Timed out" and that `cancel` was called on the agent. It does not assert that `monitorPrompt` returned within the `CANCEL_TIMEOUT_MS` bound, nor that the shared process was not killed. Because the shared path does not expose an `acpProcess` handle (and the shared agent is a fake with no kill path), the "no kill" assertion is vacuous — the contract would also be satisfied if the test waited 20 minutes. The companion ephemeral test (`EphemeralSessionCancelHangs_CleanupForcesProcessKill_AndReturnsWithinBound`) does assert the elapsed bound (`>= 4_500 && < 10_000`), so the shared-side bound is the missing assertion.
  SuggestedAction: Add `expect(elapsed).toBeLessThan(2_000)` (or similar) to bound the shared-path wait. Out of scope for this fix; the issue's six required test cases are all present.
  Status: follow-up

- [ID: fu-4]
  Severity: follow-up
  Scope: `packages/runner/src/actions/acp-agent.ts:1050`
  Evidence: After `Promise.race` resolves via the `timeout(CANCEL_TIMEOUT_MS)` branch, the still-pending `connection.cancel(...).then(() => { cancelled = true })` may resolve later and set `cancelled = true` after the `if (!cancelled && acpProcess)` check. The race itself is correct and the cleanup is idempotent, so the side effect is benign. No bug, but the boolean is named "cancelled" yet really reflects "did we run cleanup" by the time of the check.
  SuggestedAction: Optionally rename to `timedOut` / `didForceCleanup` for clarity. Out of scope.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: pe-1]
  Severity: info
  Scope: `dotnet test Mohist.sln`
  Evidence: 513 of 1093 server tests fail on the current branch with `Microsoft.EntityFrameworkCore.Migrations.PendingModelChangesWarning` originating from `Mohist.Server.Tests.Specs.Workflow.Grain.BacklogFixture.InitializeAsync` and `Mohist.Server.Tests.Specs.Workflow.WorkflowGrainFixture.InitializeAsync`. Confirmed to be pre-existing by running the same suite against base commit `2c4889bce` (before the issue-135 changes) — same 513 failures, same module, same error. The issue's `prompt_timeout` enum member is purely additive to a `string` field on the .NET side (`AgentSessionGrain.cs:329`, `AgentSessionQuerier.cs:339` read it via `GetStringProp`), so it cannot be the cause.
  SuggestedAction: Pre-existing migration hygiene issue (model has pending changes; new EF migration needed). Out of scope for issue #135.
  Status: pre-existing

- [ID: pe-2]
  Severity: info
  Scope: `packages/runner/tests/acp-agent.spec.ts`
  Evidence: The test file imports `mkdtemp`, `rm`, `writeFile` from `node:fs/promises`, `join` from `node:path`, `tmpdir` from `node:os`, `afterEach`, `describe`, `expect`, `it`, `vi` from `vitest`, several ACP SDK types, and Mohist internals. The new test fixtures (`createCancelHangingWritable`, `createTrackedFakeProcess`, `describeMessage`) are well-scoped to the test file and do not affect production code.
  SuggestedAction: None — test-only support.
  Status: pre-existing

## Spec Compliance Verification

| Acceptance gate (issue / tasks.json) | Evidence | Status |
|---|---|---|
| `npm --prefix packages/runner test -- acp-agent.spec.ts` | 47/47 pass in 6.87 s | ✅ |
| `npm --prefix packages/runner test` | 218/218 pass across 20 files in 13.30 s | ✅ |
| `npm --prefix packages/runner run typecheck` | clean (no diagnostics) | ✅ |
| `dotnet test Mohist.sln` (additive enum check) | Server-side `failureCategory` is read as `string?` (`AgentSessionGrain.cs:329`, `AgentSessionQuerier.cs:339`); no enum deserializer to break. Pre-existing migration failures unrelated to this change (`pe-1`). | ✅ |
| Web `failureCategory` switch case | No exhaustive switch exists. `SessionCard.tsx:56-58` and `SessionPage.tsx:398-402` render the value as plain text; types are `string \| null` (`entities/coder-session/model/types.ts:43,84,117,179`, `entities/agent/model/types.ts:93,172`). `prompt_timeout` will render via default fallback. | ✅ |
| Manual: `with.timeout: 5000` + log w/ provider error → error contains provider error | Covered by `PromptTimesOutWithProviderErrorInLog_ErrorMessageContainsDiagnostic_AndFailureCategoryIsPromptTimeout` (`tests/acp-agent.spec.ts:807`) — uses `timeout: 100`, mocks the log via `MOHIST_OPENCODE_LOG_DIR`, asserts both the error message and `output.providerError.statusCode === 2056`. | ✅ |
| Manual: `connection.cancel` hang → `monitorPrompt` returns within bound | Covered by `EphemeralSessionCancelHangs_CleanupForcesProcessKill_AndReturnsWithinBound` (`tests/acp-agent.spec.ts:743`) — asserts `cleanupCount() >= 1` and `4500 <= elapsed < 10000`. | ✅ |
| Bug #1 — timeout path collects diagnostic + emits `failed` event + returns enriched error | `monitorPrompt` timeout branch at `acp-agent.ts:934-948` performs all three steps symmetrically with the other failure paths. | ✅ |
| Bug #1 — `LivenessFailureReason` adds `"prompt_timeout"` | `acp-agent.ts:275` and used at lines 938, 946. | ✅ |
| Bug #2 — `cancelAndReturn` 5s hard timeout + ephemeral `acpProcess.cleanup()` | `acp-agent.ts:1050-1062` with `CANCEL_TIMEOUT_MS = 5_000` at line 40. | ✅ |
| Bug #2 — three call sites thread `acpProcess` (ephemeral) vs `undefined` (shared) | Ephemeral at `acp-agent.ts:891` (passes `acpProcess`); shared callers `runPromptOnExistingWorkflowAgentSession` (line 630), `runResumedWorkflowAgentSession` (line 708), `runNewWorkflowAgentSession` (line 790) all omit the field. | ✅ |

## Notes

- The order of operations in the timeout branch is diagnostic → emit → bounded cancel → return. This matches the issue's prescribed shape and the design's Decision 1/2.
- `findOpencodeProviderErrorDiagnostic` (`runtime/opencode-log-diagnostics.ts:31`) is bounded file I/O (≤20 log files, ≤10 MB each) and returns `undefined` on any failure via try/catch, so calling it on the timeout path cannot hang.
- `appendOpencodeDiagnostic` (`runtime/opencode-log-diagnostics.ts:62`) is a no-op when the diagnostic is absent or already present in the message, so the no-diagnostic case degrades cleanly.
- `SpawnedAcpProcess.cleanup()` (`acp-agent.ts:1278`) is idempotent: the second call sees `this.exited === true` and skips the kill block, so the double-cleanup (from `cancelAndReturn` on cancel-hang + the caller's `finally`) is safe.
- The `protocol_disconnect` / `process_exit` paths at lines 962-967 and 1011-1016 already call `findOpencodeProviderErrorDiagnostic` and emit `failed` with a diagnostic. The `probe_send_failed` path at 974-979 does the same. The `probe_timeout` paths at 991-1004 and 1017-1028 also do the same. The change makes the timeout path symmetric with all five of these.

<promise>PASS</promise>
