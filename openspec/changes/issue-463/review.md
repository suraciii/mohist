# Review Findings

## P1: Follow-up terminals re-enable the streaming indicator after clearing it

`packages/web/src/widgets/session-transcript/model/useSessionTranscript.ts:521` and `:543` call `markNewContentRef.current()` with its default `engageActivity = true` immediately after `clearStreaming()` in the `session.followup_completed` and `session.followup_failed` handlers. `markNewContent()` therefore sets `isStreaming` back to `true` and starts its two-second timer even though the follow-up has just reached its terminal event. This makes the page present an active/streaming affordance after the server has ended the operation, reintroducing the activity-state mismatch this issue is intended to remove. The terminal handlers should record new transcript content without engaging activity, and tests should assert that both completed and failed follow-ups remain idle after receipt.

<promise>FAIL</promise>
