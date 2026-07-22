# Review

## Findings

### P1: Cancel can confirm from `agent_settled` while streaming remains active

`packages/runner/src/runtime/pi/runtime.ts:279-285` awaits `watchPiStop(...).wait`, but `watchPiStop` resolves `true` solely when it sees an `agent_settled` event (`runtime.ts:523-545`). It never checks `session.isStreaming` when that event arrives or before returning. The issue contract requires stop confirmation from both the Pi streaming state and the event sequence; a stale/out-of-order event or an SDK state transition that has not cleared yet can therefore produce `stopConfirmed: true` and a clean `cancelled` response while the turn is still active. The watcher should require `agent_settled` plus `isStreaming === false` (and report false on timeout), with a regression test where the settled event is emitted while `isStreaming` remains true.

<promise>FAIL</promise>
