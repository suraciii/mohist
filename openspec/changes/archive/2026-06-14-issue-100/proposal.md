## Why

`AgentSessionGrain.AppendRuntimeEventsAsync` currently blocks the runner agent loop with two inline SQLite writes per call, has no grain-side error logging when persistence fails, and can leave state persisted while transcript rows are lost. Issue 99 confirmed the result: a session row with `ToolCallCount=13` and zero transcript rows. Deferring persistence behind an in-grain accumulator removes DB I/O from the hot path and makes failures diagnosable and retryable.

## What Changes

- **Deferred persistence for agent sessions**: `AppendRuntimeEventsAsync` returns immediately after in-memory accumulation; state and transcript saves move to a background Orleans one-shot timer (200ms due, no period).
- **Two-phase transcript accumulator**: `TranscriptAccumulator` becomes the sole buffer with `Accept()` (void), `BuildFlush()` (peek), and `CommitFlush()` (commit). Text deltas accumulate continuously across calls; parts are only cleared after both state and transcript saves succeed.
- **Retryable flush with idempotency**: `SavePartsAsync` upserts by `type + correlationKey`, so a failed flush can be retried without duplicate rows.
- **Structured error logging**: all persistence sites log `LogError` with session ID, part counts, and exception details; `_stateStore.SyncLabelsAsync` logs a `Warning` when labels become null.
- **Synchronous deactivation flush**: `OnDeactivateAsync` disposes the timer and flushes remaining state and transcript before returning, with error logging.
- **Input prompt tracking**: when `session.input` arrives, the accumulator captures prompt text, kind, and timestamp for the next flush turn.
- **Realtime fan-out stays inline**: `FanOutRealtimeAsync` timing is unchanged; only persistence is deferred.

## Capabilities

### New Capabilities

- `agent-session-persistence`: deferred, two-phase persistence of agent session domain state and transcript rows using an in-grain `TranscriptAccumulator` and Orleans timer, with structured logging and synchronous deactivation flush.

### Modified Capabilities

- `coder-session-tracking`: `AgentSessionGrain.AppendRuntimeEventsAsync` no longer performs inline DB writes; realtime fan-out remains inline and the session detail transcript remains complete after the deferred flush.

## Impact

- **Server grain** (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs`): adds `_persistTimer`, `_stateDirty`, `PersistCallback`, and synchronous deactivation flush; removes inline state and transcript saves from `AppendRuntimeEventsAsync`.
- **Server accumulator** (`packages/server/src/Mohist.Server/Sessions/Services/TranscriptAccumulator.cs`): changes `Accept()` to void, adds `BuildFlush()`/`CommitFlush()`, removes `forceFlushPending`, adds `_promptText`/`_promptKind`/`_inputCreatedAt` tracking.
- **Server stores** (`IAgentSessionStore`, `IAgentSessionTranscriptStore`): `SavePartsAsync` upserts by `type + correlationKey` for idempotent retries; `SyncLabelsAsync` gains a null-label warning.
- **Runner API** (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs`): `/api/runner/{runnerId}/sessions/.../events` response latency drops because it no longer waits for DB writes.
- **Tests**: grain and accumulator tests must account for deferred persistence (e.g., advance/timer flush) rather than asserting immediate rows.
- **Backwards compatibility**: no API or event schema changes; existing sessions remain replayable. The runner event emission contract is unchanged.
