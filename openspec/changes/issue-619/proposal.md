## Why

Slack users can reach an Agent Connection before the bound Agent is executable or while the non-disabled Connection is unavailable. The Server already detects blocked Agent readiness before creating work, but Slack currently returns a generic rejection instead of actionable setup/unavailability guidance and does not identify that guidance with the triggering message, so retries can repeat it. The canonical Agent executability and Connection state data now provide the information needed to make this boundary useful and reliable.

## What Changes

- Add a Server-authored Slack setup/unavailability nudge when admission finds the bound Agent is blocked by a confirmed configuration or executability gap, or when an enabled, non-disabled Connection is unavailable.
- Present a safe, user-facing summary to the Slack caller and expose specific Agent readiness gaps, Connection health details, and repair guidance only through the existing privileged owner/operator surfaces.
- Make the nudge actionable by using the canonical Agent readiness state, gap, next action, and repair entry point rather than duplicating readiness rules in the Slack adapter.
- Deduplicate the nudge by the stable Slack message identity so redelivery produces at most one guidance delivery for the triggering message.
- Preserve the existing admission boundary: a blocked Agent or unavailable non-disabled Connection creates no AgentJob, AgentSession, SessionInput, AgentTurn, or queued inbox work. Existing Sessions and their snapshots remain unaffected.
- Keep the existing Disabled audited-discard behavior, keep normal execution unchanged for executable or currently unknown Agents on an available Connection, and keep Connection health and Agent readiness as separate concerns.

## Capabilities

- `slack-agent-setup-nudge`: Server-owned Slack admission guidance for Agent or non-disabled Connection unavailability, including safe and privileged diagnostics, actionable repair direction, stable per-message delivery deduplication, and the guarantee that blocked requests create no execution resources.

## Impact

- **Server Slack ingress:** DM launch and channel mention launch paths under `packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs` will consume the canonical Agent executability result and the current Connection availability state before inbox or launch admission. Channel coverage includes root mentions and first mentions in unbound threads.
- **Agent readiness contract:** The Slack dispatch decision must retain enough state from `AgentReadinessService` to select the safe summary and authorized repair details without introducing a second readiness model; Connection availability remains a separate gate.
- **Slack delivery and reliability:** The Server outbox will carry a stable dispatch reference for either blocked cause, allowing duplicate Slack events to converge on one delivery while preserving existing retry and delivery-uncertain behavior.
- **Tests and documentation:** Slack DM/channel ingress specs should cover Agent readiness and non-disabled Connection unavailability, authorization-scoped detail, root/thread targeting, redelivery, and the absence of Agent resources; Slack product documentation should describe the setup-mode response and the separation between caller-safe and operator-only detail.
- **Dependencies:** No new external dependency is required. The change builds on the existing Agent executability projection, Slack Connection authorization, provider inbox, and outbox contracts.
