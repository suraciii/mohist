## Why

Slack events authored by Mohist Bots must never become new work, but the rule is not applied consistently across all ingress targets. Agent Connection ingress already classifies bot events and ignores them, while the Mohist App Manager ingress can forward a bot event without a human sender identity into validation and leave the event unacknowledged, risking redelivery and bot-to-bot feedback. This change closes that admission gap before Slack Bots are used broadly as collaborators.

## What Changes

- Apply one bot-authored-message admission rule to all Slack text ingress targets, including Agent Connections and the Mohist App Manager.
- Acknowledge and ignore every normalized Slack event identified as authored by a Bot, including a Mohist Bot's own message and messages authored by another Mohist Agent App Bot; do not route it to claims, management conversations, Agent Sessions, or Agent work.
- Evaluate the bot classification before requiring a human Slack sender identity or invoking any ingress-specific authorization and conversation logic, so bot events with no `user` field are handled as valid ignored events rather than malformed requests.
- Ensure ignored bot messages create no provider inbox entry, SessionInput, AgentJob, Session, follow-up, outbox response, or user-facing acknowledgement, and do not persist or log the message text as work input.
- Preserve existing human-message routing and existing unknown-sender rejection/ignore behavior for DMs, channel mentions, and bound-thread follow-ups.
- Add regression coverage for adapter normalization and acknowledgement, the adapter-to-Server Manager envelope, and both Server ingress paths, including the no-side-effects guarantee.

## Capabilities

- `slack-bot-message-admission`: The cross-target Slack ingress contract for identifying Bot-authored events, acknowledging and ignoring them before durable input admission, preventing self-triggering and Bot-to-Bot triggering, and preserving normal human ingress behavior across Agent Connections and the Mohist App Manager.

## Impact

- **Slack adapter (`packages/mohist-slack/`):** normalized event and transport envelopes must preserve the sender kind for every ingress target, and the Socket event flow must receive a definite ignored outcome for Bot events so Slack acknowledgement does not depend on a human sender ID.
- **Server ingress (`packages/server/src/Mohist.Server/Api/` and `Slack/Services/`):** Agent Connection and Manager ingress boundaries share the same early Bot admission semantics; Manager ingress must not validate, claim, converse, or enqueue work for Bot-authored events.
- **Tests:** update adapter event/transport tests and Slack Server specs to cover Mohist App and Agent App Bot messages, missing sender IDs, acknowledgement, and absence of inbox, outbox, Session, SessionInput, and AgentJob side effects.
- **Dependencies and persistence:** no new dependency or database schema change. Runner, Web, CLI, Agent execution, and human Slack workflows remain unchanged.
