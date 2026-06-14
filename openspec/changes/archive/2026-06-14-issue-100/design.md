## Context

`AgentSessionGrain.AppendRuntimeEventsAsync` is the only server-side entry point that receives runtime events from the coder runner. Today every call:

1. Applies the event to the in-memory `AgentSession` domain model.
2. Runs `_transcript.Accept(...)` to turn runtime rows into transcript part deltas.
3. Awaits `_stateStore.SaveAsync(...)` to persist the domain state.
4. Awaits `_transcriptStore.SaveAsync(...)` to persist transcript parts.
5. Fans out real-time events.

Steps 3 and 4 hit SQLite inline, so the runner’s event loop waits for two DB round-trips per batch. There is no try/catch around the stores, so failures surface as HTTP 500 to the runner without any grain-side log entry. State and transcript saves are independent, which allowed issue 99 to leave a session row with `ToolCallCount=13` but zero transcript rows. Finally, `OnDeactivateAsync` flushes any pending transcript without logging or surfacing failures.

This change keeps the runner contract unchanged and moves persistence off the hot path by buffering inside the grain and flushing with an Orleans timer.

## Goals / Non-Goals

**Goals:**

- `AppendRuntimeEventsAsync` returns immediately after in-memory accumulation and real-time fan-out; no DB write happens on the caller’s thread.
- `TranscriptAccumulator` becomes the single buffer for transcript data with a clear two-phase `BuildFlush()` / `CommitFlush()` interface.
- `AgentSessionGrain` defers persistence to a one-shot Orleans timer (200 ms due time, no period).
- All persistence sites log structured errors with session ID, part counts, and exception details.
- `OnDeactivateAsync` disposes the timer and flushes remaining dirty state and transcript synchronously, still logging any failure.
- `session.input` prompt text/kind/timestamp are captured and included in the next flush turn.
- `SavePartsAsync` upserts by `type + correlationKey` so retries are idempotent.
- The session detail page continues to show the complete transcript after a flush commits.

**Non-Goals:**

- No runner-side changes to event emission, batching, or fire-and-forget behavior.
- No attempt to solve SQLite single-writer contention or introduce out-of-process queues.
- No multi-turn support; each grain still handles a single `session.input` prompt.
- No change to `FanOutRealtimeAsync` timing or real-time event delivery semantics.

## Decisions

### 1. Use an Orleans one-shot grain timer instead of `Task.Run` or a periodic timer

A one-shot timer registered when state or transcript data becomes dirty gives us coalescing for free: many events arriving within 200 ms result in one combined flush. A periodic timer would keep firing after the grain is idle, wasting CPU and DB I/O. Kicking off an unawaited `Task.Run` would escape the grain scheduler and make deactivation races harder to reason about.

The timer callback (`PersistCallback`) is re-entrant-safe: it checks `_stateDirty` and `_transcript.BuildFlush()`, performs the work, and only disposes the timer when there is nothing left to flush and no dirty state.

### 2. Keep the buffer inside `TranscriptAccumulator` instead of an external queue

The accumulator already owns `_pending` text and can be extended to own `_accumulatedParts`. Adding a separate queue or dirty flag outside the accumulator would duplicate state and make the commit boundary ambiguous. The accumulator now exposes:

- `Accept(session, entries, now)` — void; appends text deltas to `_pending`, part deltas to `_accumulatedParts`, and records `session.input` info.
- `BuildFlush(session, now)` — peek; converts `_pending` into parts, combines them with `_accumulatedParts`, returns an `AgentSessionTranscriptFlush?`; clears `_pending` but keeps `_accumulatedParts` and input tracking for retry.
- `CommitFlush()` — clears `_pending`, `_accumulatedParts`, and input tracking after successful persistence.

This two-phase design lets the grain retry a failed flush without losing data: a subsequent `BuildFlush()` returns the same accumulated parts again.

### 3. Separate `_stateDirty` from transcript dirty state

Domain state mutations (`RecordActivity`, `ApplyUsage`, `ResolveModel`) set `_stateDirty = true`. Transcript mutations are detected by `BuildFlush()` returning non-null. This separation avoids writing the session row when only transcript deltas arrived, and avoids touching the transcript store when only usage counters changed. Both dirty signals register the same shared one-shot timer if it is not already active.

### 4. Make `SavePartsAsync` upsert by `type + correlationKey`

The current store already loads existing parts by type/key and updates matching rows. We formalize this as the idempotency mechanism: retried flushes rewrite the same rows, and late deltas for the same tool/text correlation extend existing rows rather than creating duplicates. No additional change-tracking table is needed.

### 5. Synchronous deactivation flush with swallowed exceptions

Orleans does not propagate exceptions from `OnDeactivateAsync`, so a failed flush must be logged rather than rethrown. The implementation disposes the timer, then runs the same persistence sequence as `PersistCallback` inline. If state or transcript save fails, the error is logged with session context and the method returns cleanly. This is the best effort available within Orleans deactivation semantics; durable delivery would require an outbox, which is out of scope.

### 6. Capture `session.input` info in the accumulator

When `Accept` sees a `session.input` row, it stores `_promptText`, `_promptKind`, and `_inputCreatedAt`. `BuildFlush` uses these values to build the turn upsert. `CommitFlush` clears them. Because multi-turn is out of scope, a later `session.input` simply overwrites the captured values.

### 7. Log `SyncLabelsAsync` null labels as a warning

`AgentSessionStore.SyncLabelsAsync` currently returns silently when labels are null, deleting existing labels without a trace. We add a `LogWarning` so callers can detect when a session’s labels disappear unexpectedly.

## Risks / Trade-offs

- **[Risk] Deferred persistence increases the window for in-memory data loss if the silo crashes before the timer fires.** -> Mitigation: the 200 ms window is small; deactivation flushes before grain collection; persistence failures retry on the next tick instead of dropping data.
- **[Risk] A long state or transcript save can delay the grain’s next activation message.** -> Mitigation: the work is still on the Orleans scheduler, and the timer callback is no heavier than the previous inline path; the difference is that the runner request no longer waits for it.
- **[Risk] Repeated timer retries on persistent DB failure could keep a grain alive and busy.** -> Mitigation: logging surfaces the failure; operators can inspect logs by session ID. We intentionally do not add exponential backoff or circuit breakers in this change to keep behavior predictable.
- **[Risk] Removing `forceFlushPending` from `Accept` changes when text deltas become parts.** -> Mitigation: text now accumulates continuously and is only flushed by `BuildFlush`, which is called by the timer. This preserves ordering and still respects correlation boundaries.

## Migration Plan

1. Update `TranscriptAccumulator` to the new `Accept` / `BuildFlush` / `CommitFlush` interface and add input tracking.
2. Update `AgentSessionGrain` to add `_persistTimer`, `_stateDirty`, `EnsurePersistenceTimer`, and `PersistCallback`.
3. Remove inline `_stateStore.SaveAsync` and `_transcriptStore.SaveAsync` calls from `AppendRuntimeEventsAsync`; set `_stateDirty` and ensure the timer is registered.
4. Update `OnDeactivateAsync` to dispose the timer and synchronously flush remaining data with logging.
5. Add the null-label warning to `AgentSessionStore.SyncLabelsAsync`.
6. Update unit tests for deferred persistence semantics.
7. Deploy server only; no runner or web changes are required.

Rollback: revert the server package. Existing persisted data is unaffected because the DB schema and transcript row identity semantics stay the same.

## Open Questions

- Should the timer due time (200 ms) be configurable per deployment, or is a hardcoded value acceptable for the current traffic volume?
- Do we need a maximum retry cap or backoff policy for the timer callback, or is unlimited retry acceptable given the small blast radius?
