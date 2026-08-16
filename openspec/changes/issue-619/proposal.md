## Why

Slack users can reach a configured Agent Connection before the bound Agent is executable. The Server already detects blocked Agent readiness before creating work, but Slack currently returns a generic rejection instead of an actionable setup nudge and does not identify that guidance with the triggering message, so retries can repeat it. The canonical Agent executability and gap data now provide the information needed to make this boundary useful and reliable.

## What Changes

- Add a Server-authored Slack setup nudge when admission finds the bound Agent is blocked by a confirmed configuration or executability gap.
- Present a safe, user-facing summary to the Slack caller and expose specific readiness gaps and repair guidance only through the existing privileged owner/operator surfaces.
- Make the nudge actionable by using the canonical Agent readiness state, gap, next action, and repair entry point rather than duplicating readiness rules in the Slack adapter.
- Deduplicate the nudge by the stable Slack message identity so redelivery produces at most one guidance delivery for the triggering message.
- Preserve the existing admission boundary: a blocked Agent creates no AgentJob, AgentSession, SessionInput, AgentTurn, or queued inbox work. Existing Sessions and their snapshots remain unaffected.
- Keep normal execution unchanged for executable or currently unknown Agents, and keep Connection health and Agent readiness as separate concerns.

## Capabilities

- `slack-agent-setup-nudge`: Server-owned Slack admission guidance for Agents that cannot accept new work, including safe and privileged readiness details, actionable repair direction, stable per-message delivery deduplication, and the guarantee that blocked requests create no execution resources.

## Impact

- **Server Slack ingress:** DM and channel-root launch paths under `packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs` will consume the canonical Agent executability result and produce the setup nudge before inbox or launch admission.
- **Agent readiness contract:** The Slack dispatch decision must retain enough state from `AgentReadinessService` to select the safe summary and authorized repair details without introducing a second readiness model.
- **Slack delivery and reliability:** The Server outbox will carry a stable dispatch reference for the nudge, allowing duplicate Slack events to converge on one delivery while preserving existing retry and delivery-uncertain behavior.
- **Tests and documentation:** Slack DM/channel ingress specs should cover blocked readiness, authorization-scoped detail, redelivery, and the absence of Agent resources; Slack product documentation should describe the setup-mode response.
- **Dependencies:** No new external dependency is required. The change builds on the existing Agent executability projection, Slack Connection authorization, provider inbox, and outbox contracts.
