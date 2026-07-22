# Review

## Findings

### P1: Cached-session validation can abort the active physical session

`packages/runner/src/runtime/pi/runtime.ts:455-464` always calls `services.openSession()` to validate a binding, even when the runner already has a cached session. When the opened handle differs from the cached handle, the code immediately calls `opened.dispose()` at line 461. The Pi SDK's `dispose()` aborts in-flight operations and releases resources; opening the same session file while `runTurn` is streaming can therefore create a second handle and disposing it can interrupt or invalidate the active handle. Follow-up and Cancel both use this path, so a valid busy Pi turn can be disrupted merely by issuing a command. Validate file existence/corruption through a non-owning SDK boundary or otherwise serialize/reuse the active handle without opening and disposing a second live session; add a busy-session regression test that proves Follow-up/Cancel validation does not abort the existing turn.

### P1: Compact event delivery is still best-effort and can be lost

`packages/runner/src/server/runner-signalr.ts:244-263` schedules `outbox.enqueueProducedFact(record)` inside the observer but does not await it or propagate failure. The SessionCommand handler can return success and journal the operation complete before the event has been durably persisted; a process crash or outbox write failure after native compaction then loses the projected Compact events, and the catch only logs the error without retrying that operation. The observer is also disabled when the outbox is not ready at line 246, while Compact still executes successfully. Because the acceptance criterion requires compaction events through the existing session event channel, Compact must either make event persistence part of the command's durable completion path or retain/retry the event through the existing recovery/outbox mechanism, with failure behavior covered by a test.

### P1: Cancel treats non-settled lifecycle events as stop confirmation

`packages/runner/src/runtime/pi/runtime.ts:275-296` uses `isPiStopEvent` to accept `turn_end`, `agent_end`, or `agent_settled` as equivalent evidence. In the pinned Pi SDK, `agent_end` can occur before retries, auto-compaction, or queued continuations have settled, and `turn_end` only ends one turn; neither proves that the session is fully stopped. If `isStreaming` is temporarily false at the immediate check after `abort()`, this implementation can return `stopConfirmed: true` on one of those earlier events even though Pi can continue processing. Stop confirmation must require the SDK's fully settled event (or an equally authoritative event sequence/state observation), and the regression test should cover `agent_end`/`turn_end` followed by continued activity reaching the API's `interruptUnconfirmed` result.

<promise>FAIL</promise>
