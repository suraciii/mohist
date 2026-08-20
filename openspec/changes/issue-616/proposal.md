## Why

Slack events authored by Mohist-managed Bots must never become new work, but the rule is not applied consistently across all ingress targets. Agent Connection ingress already classifies Bot events and partially ignores them, while Mohist App Manager ingress can forward a Bot event without a human sender identity into validation and leave it unacknowledged, risking redelivery and Bot-to-Bot feedback; this change closes that admission gap before Slack Bots are used broadly as collaborators without changing behavior for unrelated third-party Slack Bots.

## What Changes

- Classify every Slack text event as `human`, `bot`, or `unknown`, while applying the new admission rule only to Bot events attributable to a Mohist-managed Manager App or Agent App.
- Acknowledge and ignore managed Bot events across Agent Connections and Mohist App Manager ingress, including a Mohist Bot's own message and messages authored by another Mohist Agent App Bot; do not route them to claims, management conversations, Agent Sessions, or Agent work.
- Preserve managed-Bot author identity metadata through the adapter and Manager transport so the Server can distinguish Mohist-managed Bots from unrelated third-party Bots. The receiving Socket app identity is not treated as the author identity.
- Evaluate managed-Bot admission before requiring a human Slack sender identity or invoking ingress-specific authorization and conversation logic, so a managed Bot event with no `user` field is handled as a valid ignored event rather than a malformed request.
- Ensure ignored managed-Bot messages create no provider inbox entry, SessionInput, AgentJob, Agent Session, follow-up, outbox response, or user-facing acknowledgement, and do not persist or log the message text as work input.
- Preserve existing behavior for non-Mohist-managed Bot events, human messages, and unknown senders across DMs, channel mentions, and bound-thread follow-ups.
- Add regression coverage for adapter normalization and acknowledgement, author-identity propagation, the adapter-to-Server Manager envelope, both Server ingress paths, third-party Bot compatibility, and the no-side-effects guarantee.

## Capabilities

- `slack-bot-message-admission`: The cross-target Slack ingress contract for identifying Bot-authored events, acknowledging and ignoring Mohist-managed ones before durable input admission, preventing self-triggering and Bot-to-Bot triggering, and preserving normal human and third-party Bot ingress behavior across Agent Connections and Mohist App Manager.

## Impact

- **Slack adapter (`packages/mohist-slack/`):** normalized event and transport envelopes must preserve the sender kind and optional Bot author App identity for every ingress target. The Socket event flow must receive a definite ignored outcome for managed Bot events so Slack acknowledgement does not depend on a human sender ID.
- **Server ingress (`packages/server/src/Mohist.Server/Api/` and `Slack/Services/`):** Agent Connection and Manager ingress boundaries share the same early managed-Bot admission semantics. Manager ingress must not validate, claim, converse, or enqueue work for Mohist-managed Bot events, while non-managed Bot events retain their existing target-specific behavior.
- **Tests:** update adapter event/transport tests and Slack Server specs to cover Mohist App and Agent App Bot messages, author identity propagation, missing sender IDs, acknowledgement, absence of inbox, outbox, Session, SessionInput, and AgentJob side effects, and preservation of third-party Bot behavior.
- **Dependencies and persistence:** no new dependency or database schema change. Runner, Web, CLI, Agent execution, and human Slack workflows remain unchanged.
