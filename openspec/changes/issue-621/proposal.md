## Why

Slack turns can complete with useful work in the Runner while publishing no reply through `mo slack message send`, leaving the user with no conclusion or next step even though the Agent had an opportunity to communicate. The Slack contract already makes the Agent the owner of reply content and treats silence as valid, so the missing behavior is a bounded Runner advisory that catches likely omissions without inventing a message or turning silence into failure.

## What Changes

- Add a Slack-aware reply guard at the end of eligible Runner turns, covering both initial Agent work and Slack follow-up turns.
- Detect when the current Slack-bound turn reaches a terminal point without an accepted Agent reply publication, then give the model one bounded advisory using the existing reply context.
- Allow the model to publish a self-contained reply after the advisory or explicitly leave the turn silent; do not synthesize a Server-authored reply.
- Limit the guard to one advisory per turn with a bounded wait. Advisory failure, timeout, interruption, or runtime unavailability preserves the original turn outcome and does not trigger a retry loop.
- Leave non-Slack turns unchanged, and preserve the existing rule that Server liveness closeout and Agent reply content are independent.

## Capabilities

- `runner-reply-guard`: Detects an unpublished reply at the end of a Slack-bound Runner turn and issues at most one bounded, silence-licensing advisory while preserving Agent-owned reply content and the original execution outcome.

## Impact

- Affects Runner turn orchestration for the Pi and OpenCode paths, including AgentJob execution and Slack follow-up delivery, plus the existing Slack execution-context and reply-publication observation boundary.
- Adds focused Runner coverage for published replies, unpublished replies, explicit silence, duplicate-guard prevention, advisory timeout/failure, interrupted turns, and non-Slack turns.
- Reuses the existing Slack reply action, reply anchor, outbox, and liveness model; it should not add a Server-generated fallback reply, alter terminal delivery, or change Slack delivery APIs.
- No new external dependency or persistence schema is expected.
