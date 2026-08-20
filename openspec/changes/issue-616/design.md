## Context

Slack Socket events are normalized by `packages/mohist-slack/src/adapter-events.ts` into a `SlackEnvelope`, and the adapter acknowledges an event after the Server returns an ingress result. The envelope already carries `senderKind` (`human`, `bot`, or `unknown`), but it does not preserve the Bot author identity separately from the receiving Socket App identity (`apiAppId`). The Manager transport also manually projects the envelope and currently omits `senderKind` and Bot metadata.

The two Server ingress paths have different admission behavior. Agent Connection ingress currently ignores all Bot and unknown senders after basic identity checks, but it does so without distinguishing Mohist Bots from third-party Bots and performs disabled-Connection auditing before that decision. Manager ingress requires `senderSlackUserId`, accepts an inbox row, and then performs actor and conversation processing. Consequently, a managed Bot event without a human `user` field can be rejected before it is safely ignored, and a Bot event that reaches Manager can create durable work-input state.

Mohist-managed identities are already persisted: the active workspace enrollment contains the Manager App identity and Bot user identity, while managed Agent App records contain their workspace, App identity, Bot user identity, lifecycle, binding, and deletion state. The change affects the Slack adapter, both ingress boundaries, and their tests. It does not require a new dependency or database schema.

## Goals / Non-Goals

**Goals:**

- Normalize every Slack text event as exactly one sender kind and preserve author Bot metadata independently from the receiving App identity.
- Carry sender kind, optional human sender identity, and optional Bot author identity through both Connection and Manager transports.
- Apply one workspace-scoped managed-Bot admission rule to the Manager App and every registered managed Agent App, regardless of which Mohist target receives the event.
- Return `{ kind: "ignored" }` before human-sender validation, ingress-specific authorization, routing, inbox admission, disabled-event auditing, or conversation processing.
- Let the existing adapter acknowledgement path acknowledge a definite ignored result exactly once without posting or otherwise acknowledging in Slack.
- Preserve existing human, unknown, and unrelated third-party Bot behavior for each target.
- Keep ignored events read-only: no inbox, SessionInput, AgentJob, Session, follow-up, conversation action, outbox delivery, or message-text work log is created.

**Non-Goals:**

- Suppressing every Slack Bot or changing the existing target-specific handling of unrelated third-party Bots.
- Changing Slack interactions, outbound delivery, Bot enrollment, App installation, or Bot identity rotation workflows.
- Adding a durable ignored-event table, a schema migration, a Slack API lookup, or a user-facing acknowledgement.
- Adding a general event-processing framework or moving non-Slack ingress rules into this design.

## Decisions

### 1. Preserve author identity in the adapter envelope

Extend `SlackEnvelope` with optional author Bot metadata, alongside the existing `senderKind` and human-only `senderSlackUserId`. The normalized shape should carry the author Bot App identity when Slack supplies it, plus the available Bot/user identity fields needed for exact matching. The adapter may retain an opaque Slack Bot identity such as `bot_id`, but it must never treat the outer `api_app_id` as the author. `apiAppId` remains the identity of the App receiving the Socket event.

The normalizer determines sender kind from Bot markers first. A Bot event does not populate the human sender field; any Bot-specific user or profile identity is stored in the separate author metadata. Events without a Bot marker use a stable `user` as `human`; otherwise they become `unknown`.

**Alternative considered:** infer authorship in the Server from the receiving target or from `apiAppId`. Rejected because the receiving Manager App can receive another Mohist Agent's message, and the receiving identity is explicitly not evidence of authorship. Passing raw Slack payloads is also rejected because it leaks unnecessary provider data and bypasses the adapter's allowlisted envelope.

### 2. Make both transport contracts lossless and additive

Connection ingress already serializes the envelope, so `SlackIngressBody` will gain the new sender and author fields. The Manager projection in `managerIngressBody` will explicitly include the same fields, and `SlackManagerIngressBody` / `SlackManagerIngressMessage` will accept a nullable human sender for Bot events. The Manager request will continue to carry the receiving `AppId` separately from the author fields.

Missing `senderKind` remains compatible with existing direct callers by retaining the current fallback when a human sender is present. Adapter-generated events always send the explicit kind. A Manager request may omit a sender only when it is an explicitly classified Bot event; a non-managed Bot without a human identity continues through the existing rejection/ignore behavior rather than gaining a synthetic user.

**Alternative considered:** add the fields only to the Connection route and let Manager infer them from its target. Rejected because Manager must distinguish its own Bot from another managed Agent Bot and must not confuse receiver identity with author identity. Making the fields mandatory immediately is also rejected because it would break existing test and operator callers during rollout without improving the Slack adapter contract.

### 3. Centralize managed-Bot attribution in a Server admission service

Add a small Server-side service in the Slack service boundary, for example `SlackManagedBotAdmissionService`, with a read-only operation that evaluates a normalized Bot event using the workspace and author metadata. Non-Bot events and Bot events without a usable author identity return `not managed` without an identity query.

For a Bot with author metadata, the service loads the active workspace enrollment and the current registered managed Agent App identities. It matches the author App identity, and any available author Bot-user identity, against the enrolled Manager identity and non-deleted, live managed Agent App identities in the same workspace. If multiple author identifiers are present, they must be consistent with the same registered identity. The receiving App identity is never used as the author match. Deleted, unbound, or otherwise no-longer-registered identities are not admission matches.

Both ingress paths call this service at their boundary. This keeps the attribution rule authoritative and ensures a Bot from Agent A is suppressed when received by Agent B or by Manager.

**Alternative considered:** duplicate enrollment and Agent App queries in both route handlers. Rejected because the matching rule and active-identity filters would drift. Ignoring in the adapter is rejected because the adapter cannot authoritatively see all registered Mohist Apps in the workspace and would make cross-target behavior target-local.

### 4. Put suppression before all work admission

Connection ingress will validate transport authentication, lease, stable message identity, and workspace/target consistency, then evaluate managed-Bot admission before the disabled-Connection audit, sender access checks, mention/thread routing, and DM/session handling. A managed Bot immediately returns `kind = ignored`; the disabled path and the existing generic Bot/unknown branch are not reached.

Manager ingress will retain operator and runtime-lease gates plus structural target/message checks, then evaluate managed-Bot admission before the direct-message restriction, actor authentication, claim consumption, actor authorization, inbox insertion, and conversation processing. The managed branch returns a definite ignored result without calling `SlackProviderInboxStore`, `ManagerClaimService`, `ManagerActorAccessDecider`, or the conversation processor.

The existing adapter behavior is sufficient for acknowledgement: `handleEvent` does not render a user-facing rejection for an `ignored` result, acknowledges after the definite Server result, and then drains normal outbound deliveries. No new Slack response or acknowledgement record is introduced.

**Alternative considered:** accept the event into the provider inbox and mark it discarded. Rejected because the specification requires no provider inbox or work-input side effect, and it would expose the same redelivery/idempotency window as normal work. Acknowledge before calling the Server is also rejected because the adapter must only acknowledge after a definite ignored or accepted outcome.

### 5. Keep non-managed behavior on the existing branches

The managed check is a narrow predicate: `senderKind == bot` plus a matching registered author identity. Human events continue to the current Connection access/routing and Manager claim/access/conversation paths. Unknown events retain their target-specific validation or ignore behavior. Third-party Bots do not match the new predicate and therefore retain the current target-specific behavior; sender kind alone is not a suppression reason.

No message text is included in managed-Bot operational logging. If an operational diagnostic is added, it may contain only the ignored decision and stable non-content identifiers such as workspace and message identity.

## Risks / Trade-offs

- **Slack message variants expose author identity under different fields.** -> Normalize `event.app_id`, Bot profile metadata, and available Bot/user identifiers through one adapter helper; add fixtures for each supported variant. If no author identity is usable, classify the Bot as non-managed rather than guessing.
- **A receiving App identity could be mistaken for an author.** -> Keep `apiAppId` and author metadata as separate fields, never use `apiAppId` in the managed match, and test cross-target Agent-to-Manager delivery.
- **Identity registration can change while an event is in flight.** -> Resolve current active enrollment and managed App identities per ingress request instead of caching them in the adapter. Exact read-only matching makes repeated delivery deterministic with respect to current registration.
- **A partial deployment can produce mixed wire contracts.** -> Make Server fields additive and nullable, deploy the compatible Server before emitting new adapter fields, and roll back the adapter before rolling back the Server. Avoid leaving a new adapter against an old Manager Server, where Bot events without human senders would be rejected and retried.
- **Removing the durable ignored record loses an audit trail.** -> This is intentional: the no-side-effects contract forbids a work-input record. The adapter acknowledgement and stable provider message identity remain the only delivery boundary; repeated managed events are re-evaluated and ignored without creating work.
- **A false positive could suppress a real third-party Bot.** -> Require workspace scope and an exact match to a currently registered Manager or Agent App identity; do not match by display name, receiving App, or Bot marker alone. Cover unmatched App and Bot identities in regression tests.

## Migration Plan

1. Add the additive envelope, transport, and Server body fields, plus compatibility parsing for callers that do not send the new metadata.
2. Add the Server admission service and early branches in Connection and Manager ingress. Verify that managed events return `ignored` without inbox or outbox changes, including on disabled Connections.
3. Update the adapter to emit author metadata and keep its existing post-result acknowledgement path. Verify exactly one Socket acknowledgement and no WebClient mutation for ignored results.
4. Run focused TypeScript adapter/transport tests and Server unit/spec tests, then deploy the adapter and Server together once the compatibility checks pass. No data backfill or schema migration is needed.

Rollback is code-only. First stop or roll back the adapter so it no longer emits the new contract, then roll back the Server if necessary. If an emergency requires the old Server while the new adapter is still running, expect Manager Bot events without human senders to be rejected and retried; do not use that mixed state as the normal rollback path. Existing inbox, session, and outbox data is untouched by this feature.

## Open Questions

- Which Slack payload variants are guaranteed in production for the author App identity: `event.app_id`, `bot_profile.app_id`, or both? Capture representative fixtures before finalizing the normalizer's precedence and whether the optional Bot-user identity is available.
- Should the active Agent App match require exactly `AppLifecycle = created` and `BindingState = bound`, or should any non-deleted identity-bearing registration count while reconciliation is in progress? The stricter state filter avoids stale identities; the broader filter better handles short-lived lifecycle races.
- Is a low-cardinality metric for `managed_bot_ignored` required? It must remain content-free and cannot become a persisted ignored-event record; otherwise no new operational logging is part of this change.
