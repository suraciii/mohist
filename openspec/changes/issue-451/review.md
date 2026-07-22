# Review

## Findings

### P1: Aborted workflow turns release the Pi prompt mutex too early

`packages/runner/src/runtime/pi/runtime.ts:141-144` wraps `session.prompt()` in a `Promise.race` with `fixedSignal`. When cancellation, the deadline, or provider-retry handling resolves `fixedSignal`, `runTurn` returns from `withSessionLock` even if the underlying `session.prompt()` is still pending. The mutex therefore becomes available while Pi still has an in-flight prompt, and a queued Follow-up or Compact can issue another prompt/compact concurrently. This violates the per-physical-session serialization requirement and can recreate the SDK collision that the mutex is intended to prevent. Keep the lock held until the SDK prompt settles, while still returning the terminal turn result promptly, and add a regression test that aborts a still-pending prompt before starting another prompt-initiating operation.

### P1: Cached sessions bypass the required missing-session Reset failure

`packages/runner/src/runtime/pi/runtime.ts:446-452` returns a cached `PiSdkSession` without checking whether its bound session file still exists or can be opened. After a Pi session has been cached, deleting or invalidating its file leaves Follow-up, Compact, and Cancel operating on the stale in-memory object and potentially reporting success instead of returning `missing-session` with a Reset hint. The acceptance criterion applies when the bound file no longer exists regardless of cache state. The resolution path must validate/reopen the persisted binding (or otherwise detect stale cached files) before executing these commands, and a test must cover deletion after the session has been cached.

### P1: Production Compact drops Pi compaction events

`packages/runner/src/server/runner-signalr.ts:223-239` routes `SessionCommand` through `callSessionCommand(..., null)`. `callSessionCommand` passes that null observer to `PiRuntime.compact` (`packages/runner/src/server/command-runtime.ts:221-231`), while `PiRuntime.compact` only forwards projected `compaction_start`/`compaction_end` events through the optional observer (`packages/runner/src/runtime/pi/runtime.ts:341-348`). As a result, the production compact path invokes native compaction but emits no events into the runtime-event outbox/session event channel; the existing test only proves projection when an observer is manually supplied. Wire the command path to the session event outbox (with the same target and acknowledgement semantics as Follow-up events) and add an end-to-end handler test asserting the events are persisted.

### P1: Cancel declares confirmation without checking the Pi event sequence

`packages/runner/src/runtime/pi/runtime.ts:275-288` determines `stopConfirmed` solely from the immediate post-`abort()` value of `session.isStreaming` (after one microtask). The issue design and channel specification require confirmation from streaming state and the event sequence, specifically so a stop whose outcome is not yet observable is reported as unconfirmed. A Pi session can clear the flag before its terminal event is observed, or otherwise leave the event sequence inconclusive, and this implementation will incorrectly return `stopConfirmed: true`. Track the relevant event sequence/terminal stop evidence before marking the interruption confirmed, and test the inconclusive-event case through the API-facing `interruptUnconfirmed` result.

## Verification

- `npm run typecheck -w packages/runner`
- `npm test -w packages/runner`
- `npm test`

All commands passed, but the missing regression cases above are not covered by the current suite.

<promise>FAIL</promise>
