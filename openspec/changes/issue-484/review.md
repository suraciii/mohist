# Review

## Findings

### 1. High: Duplicate Follow-up delivery can submit the prompt twice

`packages/runner/src/server/followup-handler.ts:148-183` uses `payload.operationId` only when constructing event records and activity payloads. It does not consult the existing `SessionCommandJournal` or any other idempotency store before calling `enqueueFollowupInput` and `callFollowup`. A repeated `ReceiveFollowup` delivery with the same operation ID therefore enqueues a second `session.input` and invokes the runtime a second time. This violates the issue requirement that an input which may already have been submitted is never replayed and T-003's exactly-once Follow-up requirement. Add an operation-level admission/journal check that returns the prior result or refuses the duplicate before either input recording or runtime submission.

### 2. High: Confirmed Follow-up failures leave the session permanently `unknown`

`packages/runner/src/server/followup-handler.ts:161-180` records `activity: "unknown"` for every rejected `callFollowup` result and every thrown/rejected runtime call. The activity contract requires a completed or definitively failed/cancelled execution to return the AgentSession to `idle`; `unknown` is reserved for uncertain input acceptance or uncertain stopping. After a normal runtime rejection, this code leaves the session non-idle, so subsequent Follow-up, Reset, or recovery is rejected even though the execution has ended. Classify only genuinely uncertain outcomes as `unknown`; confirmed Follow-up rejection/failure must emit `idle` while preserving diagnostics.

### 3. High: A stale Runner disconnect can invalidate a newer connection

`packages/server/src/Mohist.Server/Runner/Services/SignalR/RunnerConnectionTracker.cs:12-27` stores only one connection ID per Runner, but `Unregister(string runnerId)`/`UnregisterAndGetSessions(string runnerId)` remove the entry without checking which connection disconnected. If a Runner reconnects with the same Runner ID before the old SignalR connection's `OnDisconnectedAsync` runs, the old disconnect removes the new connection and returns the session set to `RunnerHub`, which then calls `RunnerDisconnectedAsync` and changes active sessions to `unknown` despite the Runner being connected. This breaks the runner-restart/reuse semantics and the watchdog's intended meaning. Unregister must be conditional on the disconnecting connection ID, and session tracking must remain associated with the current connection.

## Verification

The current automated suites pass: runner 1,379 tests, web 5,127 tests with one skipped test, and the full .NET solution tests (5,684 tests across the reported projects).

<promise>FAIL</promise>
