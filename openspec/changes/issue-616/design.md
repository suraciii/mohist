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

Extend `SlackEnvelope` with nullable `authorBot` metadata, alongside the existing `senderKind` and human-only `senderSlackUserId`. `authorBot` is an allowlisted value with `appId`, `botId`, optional `botUserId`, and `identityConflict`; it carries no raw Slack object or message text. `apiAppId` remains the identity of the App receiving the Socket event and is never copied into `authorBot`.

The normalizer determines sender kind from Bot markers first. For a Bot event, `authorBot.appId` uses `event.app_id` when present, otherwise `event.bot_profile.app_id`; if both are present they must be equal or `identityConflict` is true and the event cannot match a managed identity. `authorBot.botId` uses `event.bot_id` when present, otherwise `event.bot_profile.id`, with the same disagreement rule. A Bot event's optional `event.user` is stored as `authorBot.botUserId`, never as the human sender. If both an App ID and Bot-user ID are available, the Server must require them to identify the same registered record; if only one is available, that one may establish an exact match. Events without a Bot marker use a stable `user` as `human`; otherwise they become `unknown`.

This is the production payload contract for both supported Mohist Slack Apps. The Manager fixture uses a `subtype=bot_message` event with `bot_profile.app_id` and `bot_id`; the Agent fixture uses the same shape with `event.app_id` as the App-ID source. Each fixture also includes the persisted matching App ID, and a variant with `event.user` includes the persisted matching Bot-user ID. Both supported fixtures are required to expose a matchable `authorAppId` through one of the two App-ID fields; a fixture without either field is a release-blocking contract failure, not an accepted way to classify a supported Mohist Bot as unrelated. The normalizer tests assert that both fixtures produce a matchable author identity, that the receiving outer `api_app_id` is different metadata, and that conflicting duplicate fields set `identityConflict` rather than selecting an unsafe author.

**Alternative considered:** infer authorship in the Server from the receiving target or from `apiAppId`. Rejected because the receiving Manager App can receive another Mohist Agent's message, and the receiving identity is explicitly not evidence of authorship. Passing raw Slack payloads is also rejected because it leaks unnecessary provider data and bypasses the adapter's allowlisted envelope.

### 2. Make both transport contracts lossless and additive

Connection ingress already serializes the envelope, so `SlackIngressBody` will gain `SenderKind`, nullable `SenderSlackUserId`, and nullable `AuthorBot` fields matching the `SlackBotAuthorMetadata` shape. The Manager projection in `managerIngressBody` will explicitly include `senderKind`, nullable `senderSlackUserId`, and the same `authorBot` fields, and `SlackManagerIngressBody` / `SlackManagerIngressMessage` will accept a nullable human sender for Bot events. The Manager request will continue to carry the receiving `AppId` separately from the author fields.

Missing `senderKind` remains compatible with existing direct callers by retaining the current fallback when a human sender is present. Adapter-generated events always send the explicit kind. A Manager request may omit a sender only when it is an explicitly classified Bot event; a non-managed Bot without a human identity continues through the existing rejection/ignore behavior rather than gaining a synthetic user.

**Alternative considered:** add the fields only to the Connection route and let Manager infer them from its target. Rejected because Manager must distinguish its own Bot from another managed Agent Bot and must not confuse receiver identity with author identity. Making the fields mandatory immediately is also rejected because it would break existing test and operator callers during rollout without improving the Slack adapter contract.

### 3. Centralize managed-Bot attribution in a Server admission service

Add a small Server-side service in the Slack service boundary, for example `SlackManagedBotAdmissionService`, with a read-only operation that evaluates a normalized Bot event using the workspace and author metadata. Non-Bot events and Bot events without a usable author identity return `not managed` without an identity query.

For a Bot with author metadata, the service loads the active workspace enrollment and the current managed Agent App identities. The enrolled Manager identity is eligible when both `ManagerAppId` and `ManagerBotUserId` are present. An Agent App identity is eligible when its enrollment and workspace match, `DeletedAt` is null, `AppLifecycle` is not `deleted`, and both `AppId` and `BotUserId` are present. `BindingState` is intentionally not an eligibility filter: `pending`, `in_progress`, `bound`, `connection_deleted`, and `conflict` all remain suppressible while the persisted Bot identity is non-deleted. This accepts a Bot during binding, connection-deletion, and App-deletion races, which is required by the issue's "any Agent App Bot" scope; a deleted/tombstoned or identity-less record is not a registered author.

The service matches the author App identity, and any available author Bot-user identity, against one eligible Manager or Agent registration in the same workspace. If multiple author identifiers are present, they must be consistent with the same registered identity; source-field conflicts or identifiers that point to different registrations return `not managed` and never fall back to the receiving App. The receiving App identity is never used as the author match.

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

- **Slack message variants expose author identity under different fields.** -> Use the closed precedence and conflict rule in Decision 1 for `event.app_id`, `bot_profile.app_id`, `bot_id`, `bot_profile.id`, and optional `user`; fixtures cover Manager and Agent payloads and their alternate App-ID sources. If no author identity is usable or fields conflict, classify the Bot as non-managed rather than guessing.
- **A receiving App identity could be mistaken for an author.** -> Keep `apiAppId` and author metadata as separate fields, never use `apiAppId` in the managed match, and test cross-target Agent-to-Manager delivery.
- **Identity registration can change while an event is in flight.** -> Resolve the current active enrollment and non-deleted, identity-bearing Agent App registrations per ingress request instead of caching them in the adapter. Binding and deletion-transition states remain eligible until the identity is explicitly deleted, so repeated delivery is deterministic with respect to the current registration state.
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

The author-field precedence, conflict handling, and Agent App eligibility rule are decisions in this design, not implementation-time questions. The only remaining operational question is whether a low-cardinality `managed_bot_ignored` metric is required. If added, it must remain content-free and cannot become a persisted ignored-event record; otherwise no new operational logging is part of this change.
