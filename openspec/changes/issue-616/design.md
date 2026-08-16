## Context

Issue 616 closes an admission gap in Slack text ingress. The proposal and the `slack-bot-message-admission` specification require every normalized Slack message to carry an authoritative sender classification: `human`, `bot`, or `unknown`. That classification describes the Slack sender; it is not by itself the policy decision. Only Bot-authored events attributable to a Mohist-managed Manager App or Agent App, including the Mohist Manager App Bot and any Mohist Agent App Bot, must be acknowledged and ignored before they can enter authorization, conversation routing, or durable work admission. Unmatched third-party Bot events retain the target-specific behavior that existed before issue 616.

The Slack adapter already derives `senderKind` in `normalizeSocketEvent`, and the Agent Connection route has a partial sender-kind check. The remaining boundary problems are:

- `HttpAdapterTransport.managerIngressBody` reduces the envelope but currently omits `senderKind` and the Bot author metadata needed to distinguish a Mohist-managed Bot from a third-party Bot.
- The Manager HTTP route requires `senderSlackUserId` before it can accept a managed Bot event that legitimately has no `user` field.
- Agent Connection Bot handling occurs after disabled-Connection auditing, so a managed Bot event can create a provider inbox record before it is ignored.
- The adapter acknowledges message events only after ingress returns a result, so both ingress targets must return a definite `ignored` result for managed Bot events. User-facing Slack responses must remain limited to existing backpressure handling.
- The Server must resolve all identity-bound Mohist Manager and Agent App identities in the message workspace. The target's receiving App identity (`apiAppId`) is not evidence that the message author is that App.

The affected boundaries are `packages/mohist-slack/`, the Slack adapter transport contracts, `SlackConnectionRoutes`, `SlackManagerIngressRoutes`, and `SlackManagerIngressService`. No database schema, dependency, Agent execution, or outbound-delivery change is required.

## Goals / Non-Goals

**Goals:**

- Make Slack adapter normalization the single source of sender classification, with Bot markers taking precedence over a supplied user ID.
- Preserve `senderKind` and optional Bot author App metadata in the Manager reduced envelope and in the Server-side Manager ingress message.
- Admit Mohist-managed Bot messages as valid ignored deliveries after operator authentication, runtime-lease validation, stable identity validation, and a read-only managed-identity check, without requiring a human sender ID.
- Evaluate managed-Bot admission before disabled-Connection auditing, owner/access checks, Manager actor authentication, claim consumption, conversation/session lookup, follow-up routing, and inbox admission.
- Preserve the existing target-specific behavior for a Bot classification that does not match a managed Mohist App identity.
- Return `ignored` to the adapter so Socket Mode acknowledges the event without posting a Slack message.
- Guarantee that ignored managed-Bot events create no inbox row, SessionInput, AgentJob, AgentSession, session mapping, follow-up, outbox row, or stored work text.
- Preserve existing human routing and target-specific unknown-sender behavior for DMs, mentions, and bound-thread follow-ups.
- Add focused adapter, transport, Manager ingress, Agent Connection ingress, disabled-Connection, follow-up, and no-side-effects regression coverage.

**Non-Goals:**

- Filtering Slack interactions, outbound deliveries, or non-text event types.
- Replacing Slack transport authentication, runtime leases, access policies, Manager claims, or conversation processing.
- Applying this new admission policy to non-Mohist-managed third-party Bots. Their existing sender validation, authorization, audit, or ignore behavior remains target-specific.
- Auditing ignored managed-Bot messages in the provider inbox or retaining their message text.
- Adding a database migration or a new persistence model.
- Changing human Slack workflows, unknown-sender semantics, or Slack retry behavior for transport/server failures.

## Decisions

### 1. Use an explicit sender classification as the ingress contract

`SlackSenderKind` remains a lower-case wire value with exactly three values: `human`, `bot`, and `unknown`. `normalizeSocketEvent` applies the following precedence:

1. `bot_id` or `subtype == bot_message` produces `bot`, even if Slack also supplies `user`.
2. A non-empty `user` without Bot markers produces `human`.
3. No user and no Bot marker produces `unknown`.

The normalized `SlackEnvelope` always carries `senderKind`. For a Bot event it also carries optional `senderBotAppId`, extracted from the event's Bot author metadata such as `bot_profile.app_id`; it must never be populated from the outer Socket envelope's `api_app_id`, which identifies the receiving App. `senderSlackUserId` retains the existing `event.user` value and remains null when Slack supplies no `user`. The complete Connection envelope and the Manager transport projection both preserve `senderKind` and `senderBotAppId` unchanged. A Bot message with no `user` therefore arrives as `senderKind = bot`, `senderSlackUserId = null`, and its author App metadata when Slack supplies it.

The Server uses a read-only managed-identity resolver for the message workspace. Its set contains the active enrollment's `ManagerAppId` and `ManagerBotUserId`, plus the `AppId` and `BotUserId` of every non-deleted identity-bound `ManagedSlackAgentApp` in that workspace, including apps whose Connection is currently disabled. A Bot is eligible for the new ignored branch when its `senderBotAppId` matches a managed App ID, or when its supplied `senderSlackUserId` matches a managed Bot user ID. This is workspace-wide so an Agent Connection also ignores a message authored by another Mohist Agent App Bot. A `bot` classification without either managed-identity match is not treated as managed and follows the existing target-specific path.

Server-side parsing must preserve an explicit `unknown` value and must never promote an explicitly unknown event to `human` based only on a sender ID. Existing direct callers that omit the new fields may retain the current compatibility behavior only where required by established API tests; adapter-generated envelopes are always explicit about `senderKind`, and Bot events with no author identity remain unmatched rather than being guessed as Mohist-managed. This compatibility path must not affect an explicit `unknown` value or an unmatched third-party Bot.

**Alternative considered: infer Bot ownership from `senderKind` alone in each Server ingress.** Rejected because `senderKind = bot` includes unrelated third-party Bots and does not identify the Mohist-managed author. The adapter carries only the minimal author App metadata needed for the Server's shared resolver; it does not forward the raw Slack event.

**Alternative considered: let the adapter drop Bot events locally.** Rejected because the Server must own the managed-Bot admission contract after lease validation, direct Server calls still need protection, third-party Bot behavior must remain target-specific, and the adapter needs a definitive Server result to decide when it is safe to acknowledge the Socket event.

### 2. Apply managed-Bot admission before target-specific work admission

Use the same ordering at both ingress boundaries:

```text
operator authentication
  -> runtime lease validation
  -> non-side-effecting message identity validation
  -> read-only managed-Bot identity resolution
  -> sender classification admission
       bot + managed Mohist identity -> ignored
       bot + no managed identity    -> existing target-specific Bot flow
       human                        -> existing target-specific flow
       unknown                      -> existing target-specific unknown behavior
```

For Agent Connection ingress, the managed-Bot branch must run before the disabled-Connection audit, access-policy evaluation, channel or DM routing, claim handling, session lookup, attachment binding, inbox acceptance, and outbox creation. A valid managed Bot message returns `{ kind: "ignored" }` without inspecting or changing the existing session and thread state. An unmatched third-party Bot continues through the pre-616 Connection classifier, including its existing disabled-Connection audit behavior. The identity check and managed-identity lookup may reject or fail for a malformed/unavailable request, but neither may persist the message.

For Manager ingress, the route must stop requiring `SenderSlackUserId` before managed-Bot admission and must forward `SenderKind` plus `SenderBotAppId`. After lease validation and stable identity validation, the service resolves workspace-managed identities and performs the early guard before enrollment authorization, Manager actor lookup, claim consumption, inbox acceptance, or conversation processing. `SlackManagerIngressResult.Ignored()` has no inbox identifier and no delivery-intent flag. An unmatched third-party Bot retains the existing Manager sender-ID validation and authorization path; in particular, a third-party Bot without a sender ID is not converted into the new ignored result.

A shared Server-side sender-kind parser and managed-Bot identity resolver prevent the two routes from acquiring different string or ownership semantics. The admission branches themselves remain at their respective ingress boundaries because the two targets have different DTOs and work-routing services.

**Alternative considered: put the rule only in `SlackManagerIngressService` and the existing Connection route.** Rejected because the Manager HTTP route would still reject a managed Bot with no sender ID before reaching the service, the Connection route would continue to audit managed Bot events for disabled Connections, and the two paths could resolve managed ownership differently.

**Alternative considered: run the managed-Bot check after authorization and conversation lookup.** Rejected because those operations can consume claims, call Slack APIs, create mappings, or admit work before the event is known to be ignored.

### 3. Treat `ignored` as a successful protocol outcome, not a Slack response

`SlackAdapter.handleEvent` continues to normalize the Socket payload, call the target ingress, and acknowledge after a successful ingress result. The existing rejection renderer remains active only for `backpressured`; it must not post anything for `ignored`. Thus a managed Bot event follows:

```text
Socket message -> normalized managed-Bot envelope -> Server ignored -> Socket ack
```

Transport errors, lease failures, malformed stable identities, and other Server failures remain unacknowledged so Slack can retry according to the existing behavior. A retry of a managed-Bot event is harmless because the early branch has no durable side effects, but a successful `ignored` result is what prevents a normal managed-Bot delivery from being retried merely because it has no human sender ID.

Adapter tests will assert all of the following together for a managed-Bot fixture: the Bot envelope reaches the transport with its author App metadata, the result is `ignored`, the Socket event is acknowledged, and the fake Web client receives no user-facing post. A third-party Bot fixture must still reach the transport with `senderKind = bot` and must not be locally dropped. Transport tests will assert that Manager requests contain the same `senderKind` and author metadata as the source envelope.

**Alternative considered: acknowledge bot events before calling Server.** Rejected because Server lease validation and ingress admission would be bypassed, and the adapter could acknowledge an event that the target is not currently allowed to process.

### 4. Make the no-side-effects boundary explicit and testable

The managed-Bot ignored branch returns before any operation that can create or mutate work. Tests will record baseline counts and mappings, submit managed Bot messages, and verify that they are unchanged for:

- `SlackProviderInboxRows` and Manager inbox entries
- `AgentSessions`, `SessionInput`, and `AgentJobs`
- DM current-session mappings and thread-session mappings
- Slack outbox rows and any user-facing response
- Disabled-Connection audit rows

Coverage will include a Manager App Bot direct message with no sender ID and matching author App metadata, an Agent App Bot channel mention from another managed Agent App, a managed-Bot follow-up in an already-bound thread or DM session, and a managed-Bot event routed to a disabled Connection. Separate third-party Bot cases will verify the old Manager sender validation/authorization path and the old Connection path. The test text will be a unique sentinel so accidental persistence or work-input logging is detectable.

No managed Bot event is stored merely for audit. This is required by the specification and also keeps Slack retry idempotency simple: repeated ignored deliveries have no inbox identity to deduplicate because they never become work input. A third-party Bot that follows an existing audit path remains governed by that pre-616 behavior.

**Alternative considered: create an ignored inbox row for observability.** Rejected for managed Bots because the contract explicitly forbids a provider inbox entry and storing the message as work input; operational observability can use aggregate ingress outcome metrics without message text if needed later.

## Risks / Trade-offs

- [An older adapter may omit `senderKind` or `senderBotAppId`, causing a compatibility caller to follow the legacy path while an explicitly unknown event must remain non-human] -> Deploy the adapter and Server contract together, update all adapter fixtures to send an explicit classification and author metadata for managed-Bot cases, and test explicit `unknown` separately from omitted legacy fields.
- [A Bot event with a supplied Slack `user` could be mistaken for a human by a future normalizer change] -> Keep Bot-marker precedence in one normalization function and add cases for both `bot_id` and `bot_message` with and without `user`.
- [Slack's Bot author metadata could be confused with the receiving Socket App identity, causing every Bot received by a Mohist App to be treated as managed] -> Extract `senderBotAppId` only from Bot author metadata such as `bot_profile.app_id`; never use the outer `api_app_id`, and add a third-party Bot propagation test.
- [A managed Bot from another Agent App could be treated as third-party if the resolver checks only the current target] -> Resolve the complete workspace-wide set of Manager and non-deleted Managed Agent App identities and test cross-Agent Bot-to-Bot delivery.
- [A refactor could move the managed-Bot check below disabled auditing or another side effect] -> Keep the admission order documented at the route boundary and cover disabled, bound-thread, and existing-session managed-Bot cases with database no-side-effect assertions.
- [The Manager reduced DTO could silently lose the classification or author metadata again] -> Treat `senderKind` and `senderBotAppId` as required contract members where present and assert the serialized HTTP request body in transport tests.
- [A successful Server response could be lost before Socket acknowledgement] -> Slack may retry, but the retry remains harmless because managed-Bot processing performs no durable work. Server and adapter tests should cover repeated managed-Bot delivery and the normal error/unacknowledged path.
- [Making missing classifications or author metadata unknown could affect non-adapter callers] -> Keep compatibility fallbacks narrowly scoped to omitted fields, never to explicit `unknown`, and never infer Mohist ownership from missing author metadata; coordinate the release with direct HTTP test and caller updates.

## Migration Plan

1. Update adapter types and normalization tests, then ensure `senderKind` and optional `senderBotAppId` are forwarded by both Connection and Manager transport requests without using the receiving `apiAppId` as author identity.
2. Add the Server-side wire fields and shared parsing/resolver, move Agent Connection managed-Bot admission before disabled auditing, and remove the Manager route's unconditional sender-ID requirement only for a matched managed Bot; unmatched third-party Bots retain their existing paths.
3. Add the Manager service early-return result and verify that both routes return `ignored` before inbox, claims, access checks, session processing, or outbox work for managed Bots.
4. Add the no-side-effects, human/unknown, and third-party compatibility regression suites, including Manager App Bot and Agent App Bot messages, cross-Agent Bot messages, retries, disabled Connections, and bound sessions.
5. Deploy the adapter and Server contract as one coordinated release. No database migration or data backfill is needed. During rollout, stop or drain Socket adapters while the two components are updated so a new Server does not receive legacy envelopes and a new adapter does not send bot envelopes to the old Manager contract.
6. Verify adapter package tests and the focused Slack Server specs, followed by the repository's normal fast and full verification gates.

Rollback is a coordinated rollback of the adapter and Server binaries. Since this change adds no persisted values or schema, reverting before the new contract is in use is straightforward. If a mixed-version deployment is unavoidable, stop the adapter during the transition rather than allowing managed Manager Bot events to be retried by an older route that still requires a sender ID. Unmatched third-party Bot behavior remains unchanged throughout the rollout.

## Open Questions

None remain for implementation. The wire values are lower-case `human`, `bot`, and `unknown`; `senderBotAppId` is optional author metadata distinct from `apiAppId`; only a managed-identity match activates non-persistent, non-user-facing Bot admission; and no migration is required.
