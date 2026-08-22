## Why

Slack currently has separate failure paths when a requested Agent cannot accept new work: an unconfigured Agent may return a Server-authored reply, while an unavailable or backpressured Connection may be answered directly by the adapter. Under Slack redelivery, concurrent admission, or uncertain delivery, these paths can produce no clear response or duplicate explanations; a single durable, deduplicated setup/unavailability nudge is needed now so users receive one trustworthy next step without creating work that cannot run.

## What Changes

- Block new-work admission for an ordinary DM without a current Session, an explicit leading `new task` DM even when a current Session exists, a channel root mention, or the first mention in an unbound thread when the target Agent is not ready or a non-Disabled Connection is unavailable; classify the DM marker before any backpressure short-circuit.
- Create one durable setup/unavailability nudge for the blocked event, without creating a Session, SessionInput, Turn, AgentJob, or pending execution inbox work.
- Show ordinary callers a safe summary that does not disclose configuration details, credentials, internal errors, or repair commands. Keep concrete readiness gaps, Connection state, and next actions available through the existing authorized diagnostic surfaces for Owners and authorized operators, using the same canonical execution-readiness result that admission uses so history-based non-executable gaps remain visible.
- **BREAKING (internal adapter contract):** make ingress outcomes explicitly identify the owner of the user-visible response. When Server has created a durable nudge, the adapter must not send a second direct backpressure message; direct adapter messaging remains only for the existing backpressure path where no durable delivery intent was created.
- Deduplicate the nudge and its delivery across ordered Slack redeliveries, concurrent admission attempts, delivery uncertainty, and reconciliation so one Slack event produces at most one durable intent and one user-visible message.
- Preserve existing behavior for ordinary follow-ups to an established Session, while treating the explicit leading `new task` DM marker as new work even when a DM Session mapping exists; also preserve Disabled Connections and their audited discard semantics, executable Agents, and `unknown` readiness. Do not change readiness rules, add automatic repair, or create setup guidance for Disabled Connections.
- Add end-to-end coverage that sends a real Slack ingress request through the Server and the real Node adapter event handler, including the durable-outbox and direct-send paths and their no-duplication boundary.

## Capabilities

- `slack-agent-admission-nudge`: Admission behavior for Slack-originated new work when Agent readiness or Connection availability prevents execution, including safe caller guidance, authorized diagnostics, no-execution side effects, and preservation of follow-up, Disabled, executable, and unknown-readiness behavior.
- `slack-ingress-response-ownership`: The Server-to-adapter contract for identifying whether a blocked Slack event is answered by a durable outbox intent or by the adapter's direct backpressure fallback, with idempotent convergence across redelivery, concurrency, uncertain delivery, and reconciliation.

## Impact

- **Server Slack ingress and admission:** Connection DM/channel routing, the canonical Agent execution-readiness and Connection diagnostic boundaries, and the blocked-admission result contract will change. The authorized diagnostic endpoint will expose the same concrete executability gaps and next actions used by admission. Durable Slack outbox/nudge identity and deduplication will be used for setup/unavailability guidance.
- **Slack adapter:** `packages/mohist-slack` event handling, ingress result types, and direct rejection rendering must honor Server response ownership and avoid duplicate posts.
- **Persistence and delivery:** Existing Slack inbox/outbox and delivery reconciliation paths must retain one stable nudge intent through retries and uncertain outcomes; Disabled audited discard remains separate.
- **Tests:** Server Slack integration/spec tests and adapter tests must run together for DM, channel-root, and unbound-thread cases, including concurrent/redelivered events, delivery uncertainty, safe-message visibility, and the legacy direct backpressure fallback.
- **Unaffected systems:** Agent readiness criteria, Agent execution and Session follow-up semantics, CLI/Web contracts except for existing diagnostic observations, external dependencies, and automatic configuration repair remain unchanged.
