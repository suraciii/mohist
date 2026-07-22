# Review

## Findings

### P1: Cancel does not wait for the settled event before deciding

`packages/runner/src/runtime/pi/runtime.ts:275-296` subscribes for `agent_settled`, calls `abort()`, waits for only one microtask, and immediately computes `stopConfirmed`. Pi's fully settled event is emitted after the agent operation has finished all post-run work, and it may arrive asynchronously after the abort promise resolves. A genuinely successful abort can therefore be returned as `stopConfirmed: false`/`interruptUnconfirmed: true`, while the code never observes the later settlement because it unsubscribes at line 294. The cancel path must await the settled event (with an explicit bounded timeout/failure-to-confirm path) before returning, and the regression test must emit `agent_settled` asynchronously after `abort()` resolves and assert a confirmed cancel.

### P1: Cached-session validation does not prove that Pi can open the session

`packages/runner/src/runtime/pi/sdk.ts:115-121` implements `validateSessionFile` by checking that every non-empty line is syntactically valid JSON, and `packages/runner/src/runtime/pi/runtime.ts:459-464` trusts that result for a cached SDK handle. A deleted file is detected, but an empty file, a syntactically valid JSONL file with invalid Pi records, or a file whose session identity/path is no longer usable passes this validator and the stale cached session is still used. That violates the requirement that a bound session that cannot be opened fails with a Reset hint. The validation seam must use the Pi SDK's actual session-open/parser validation without creating and disposing a second live `AgentSession`, or validate the complete persisted session schema/identity; add a cached-session corruption test.

<promise>FAIL</promise>
