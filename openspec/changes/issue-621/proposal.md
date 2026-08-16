## Why

Slack turns can complete useful work in the Runner while publishing no reply through `mo slack message send`, leaving the user with no conclusion or next step even though the Agent had an opportunity to communicate. The Slack contract already makes the Agent the owner of reply content and treats silence as valid, so the missing behavior is a bounded Runner advisory that catches likely omissions without inventing a message or turning silence into failure.

## What Changes

- Add a Slack-aware reply guard at the actual terminal boundary of eligible Runner turns, covering both initial Agent work and Slack follow-up turns.
- Observe the existing Agent reply action locally in the Runner. The first invocation attempt counts even when the command later fails; Server acceptance and provider delivery are not part of the predicate.
- When a Slack turn reaches its terminal point without a reply action attempt, give the same Agent session a bounded advisory that asks it to publish a self-contained reply or deliberately remain silent.
- Allow at most two advisory reminders by default for one turn. Count each reminder before invoking it, stop when a reply action is attempted, and end normally after the finite budget.
- Preserve the original turn result, liveness closeout, and Agent-owned reply path under advisory success, silence, failure, timeout, interruption, or runtime unavailability.
- Leave non-Slack turns unchanged. Make no Server-side unpublished-reply detection or Server-authored fallback change.

## Capabilities

- `runner-reply-guard`: Detects the absence of a Runner-observed Slack reply action attempt at the end of a Slack-bound turn and issues up to two bounded, silence-licensing advisories while preserving Agent-owned reply content and the original execution outcome.

## Impact

- Affects Runner turn orchestration for the Pi and OpenCode paths, including AgentJob execution and Slack follow-up terminal handling, plus the existing Runner runtime-event observation boundary.
- Adds focused Runner coverage for reply action attempts, rejected sends, unpublished turns, explicit silence, the default-two reminder budget, duplicate terminal signals, advisory timeout/failure, interrupted turns, the Pi streaming follow-up terminal boundary, and non-Slack turns.
- Reuses the existing Slack execution context, reply action, reply anchor, and collaboration skill. The Runner does not query the Server outbox or delivery state and does not add a Server endpoint, Server matching logic, persistence schema, or external dependency.
- The existing Server reply action and liveness projection remain unchanged.
