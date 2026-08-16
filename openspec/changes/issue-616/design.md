## Context

Issue 616 closes an admission gap in Slack text ingress. The proposal and the `slack-bot-message-admission` specification require every normalized Slack message to carry an authoritative sender classification: `human`, `bot`, or `unknown`. Bot-authored events, including messages from the Mohist Manager App Bot and any Mohist Agent App Bot, must be acknowledged and ignored before they can enter authorization, conversation routing, or durable work admission.

The Slack adapter already derives `senderKind` in `normalizeSocketEvent`, and the Agent Connection route has a partial sender-kind check. The remaining boundary problems are:

- `HttpAdapterTransport.managerIngressBody` reduces the envelope but currently omits `senderKind`.
- The Manager HTTP route requires `senderSlackUserId` before it can accept a bot event that legitimately has no `user` field.
- Agent Connection bot handling occurs after disabled-Connection auditing, so a bot event can create a provider inbox record before it is ignored.
- The adapter acknowledges message events only after ingress returns a result, so both ingress targets must return a definite `ignored` result for bot events. User-facing Slack responses must remain limited to existing backpressure handling.

The affected boundaries are `packages/mohist-slack/`, the Slack adapter transport contracts, `SlackConnectionRoutes`, `SlackManagerIngressRoutes`, and `SlackManagerIngressService`. No database schema, dependency, Agent execution, or outbound-delivery change is required.

## Goals / Non-Goals

**Goals:**

- Make Slack adapter normalization the single source of sender classification, with bot markers taking precedence over a supplied user ID.
- Preserve `senderKind` in the Manager reduced envelope and in the Server-side Manager ingress message.
- Admit bot messages as valid ignored deliveries after operator authentication and runtime-lease validation, without requiring a human sender ID.
- Evaluate bot admission before disabled-Connection auditing, owner/access checks, Manager actor authentication, claim consumption, conversation/session lookup, follow-up routing, and inbox admission.
- Return `ignored` to the adapter so Socket Mode acknowledges the event without posting a Slack message.
- Guarantee that ignored bot events create no inbox row, SessionInput, AgentJob, AgentSession, session mapping, follow-up, outbox row, or stored work text.
- Preserve existing human routing and target-specific unknown-sender behavior for DMs, mentions, and bound-thread follow-ups.
- Add focused adapter, transport, Manager ingress, Agent Connection ingress, disabled-Connection, follow-up, and no-side-effects regression coverage.

**Non-Goals:**

- Filtering Slack interactions, outbound deliveries, or non-text event types.
- Replacing Slack transport authentication, runtime leases, access policies, Manager claims, or conversation processing.
- Auditing ignored bot messages in the provider inbox or retaining their message text.
- Adding a database migration or a new persistence model.
- Changing human Slack workflows, unknown-sender semantics, or Slack retry behavior for transport/server failures.

## Decisions

### 1. Use an explicit sender classification as the ingress contract

`SlackSenderKind` remains a lower-case wire value with exactly three values: `human`, `bot`, and `unknown`. `normalizeSocketEvent` applies the following precedence:

1. `bot_id` or `subtype == bot_message` produces `bot`, even if Slack also supplies `user`.
2. A non-empty `user` without bot markers produces `human`.
3. No user and no bot marker produces `unknown`.

The normalized `SlackEnvelope` always carries `senderKind`. The connection endpoint receives the complete envelope. The Manager transport projection continues to omit fields the Manager does not need, but it must include `senderKind` and pass it unchanged through `SlackManagerIngressBody` and `SlackManagerIngressMessage`. A bot message with no `user` therefore arrives as `senderKind = bot` and `senderSlackUserId = null`, rather than failing DTO validation.

Server-side parsing must preserve an explicit `unknown` value and must never promote an explicitly unknown event to `human` based only on a sender ID. Existing direct callers that omit the new field may retain the current compatibility behavior only where required by established API tests; adapter-generated envelopes are always explicit. This compatibility path must not affect an explicit `unknown` value.

**Alternative considered: infer bot status in each Server ingress from Slack fields.** Rejected because the normalized envelope intentionally does not carry the raw Slack event, and duplicated inference would diverge between Manager and Agent Connection targets.

**Alternative considered: let the adapter drop bot events locally.** Rejected because the Server must own the admission contract after lease validation, direct Server calls still need protection, and the adapter needs a definitive Server result to decide when it is safe to acknowledge the Socket event.

### 2. Apply bot admission before target-specific work admission

Use the same ordering at both ingress boundaries:

```text
operator authentication
  -> runtime lease validation
  -> non-side-effecting message identity validation
  -> sender classification admission
       bot    -> ignored
       human  -> existing target-specific flow
       unknown -> existing target-specific unknown behavior
```

For Agent Connection ingress, the bot/unknown branch must run before the disabled-Connection audit, access-policy evaluation, channel or DM routing, claim handling, session lookup, attachment binding, inbox acceptance, and outbox creation. A valid bot message returns `{ kind: "ignored" }` without inspecting or changing the existing session and thread state. The identity check may reject a malformed event, but it must not persist the message.

For Manager ingress, the route must stop requiring `SenderSlackUserId` before sender admission. After lease validation, it passes the classification to `SlackManagerIngressService`; the service performs the same early bot guard before enrollment authorization, Manager actor lookup, claim consumption, inbox acceptance, or conversation processing. `SlackManagerIngressResult.Ignored()` has no inbox identifier and no delivery-intent flag.

A small shared Server-side sender-kind parser can prevent the two routes from acquiring different string semantics. The admission branches themselves remain at their respective ingress boundaries because the two targets have different DTOs and work-routing services.

**Alternative considered: put the rule only in `SlackManagerIngressService` and the existing Connection route.** Rejected because the Manager HTTP route would still reject a bot with no sender ID before reaching the service, and the Connection route would continue to audit disabled bot events.

**Alternative considered: run the bot check after authorization and conversation lookup.** Rejected because those operations can consume claims, call Slack APIs, create mappings, or admit work before the event is known to be ignored.

### 3. Treat `ignored` as a successful protocol outcome, not a Slack response

`SlackAdapter.handleEvent` continues to normalize the Socket payload, call the target ingress, and acknowledge after a successful ingress result. The existing rejection renderer remains active only for `backpressured`; it must not post anything for `ignored`. Thus a bot event follows:

```text
Socket message -> normalized bot envelope -> Server ignored -> Socket ack
```

Transport errors, lease failures, malformed stable identities, and other Server failures remain unacknowledged so Slack can retry according to the existing behavior. A retry of a bot event is harmless because the early branch has no durable side effects, but a successful `ignored` result is what prevents a normal bot delivery from being retried merely because it has no human sender ID.

Adapter tests will assert all of the following together: the bot envelope reaches the transport, the result is `ignored`, the Socket event is acknowledged, and the fake Web client receives no user-facing post. Transport tests will assert that Manager requests contain the same `senderKind` as the source envelope.

**Alternative considered: acknowledge bot events before calling Server.** Rejected because Server lease validation and ingress admission would be bypassed, and the adapter could acknowledge an event that the target is not currently allowed to process.

### 4. Make the no-side-effects boundary explicit and testable

The ignored branch returns before any operation that can create or mutate work. Tests will record baseline counts and mappings, submit bot messages, and verify that they are unchanged for:

- `SlackProviderInboxRows` and Manager inbox entries
- `AgentSessions`, `SessionInput`, and `AgentJobs`
- DM current-session mappings and thread-session mappings
- Slack outbox rows and any user-facing response
- Disabled-Connection audit rows

Coverage will include a Manager App Bot direct message with no sender ID, an Agent App Bot channel mention from another Agent App, a bot follow-up in an already-bound thread or DM, and a bot event routed to a disabled Connection. The test text will be a unique sentinel so accidental persistence or work-input logging is detectable.

No bot event is stored merely for audit. This is required by the specification and also keeps Slack retry idempotency simple: repeated ignored deliveries have no inbox identity to deduplicate because they never become work input.

**Alternative considered: create an ignored inbox row for observability.** Rejected because the contract explicitly forbids a provider inbox entry and storing the message as work input; operational observability can use aggregate ingress outcome metrics without message text if needed later.

## Risks / Trade-offs

- [An older adapter may omit `senderKind`, causing a compatibility caller to follow the legacy path while an explicitly unknown event must remain non-human] -> Deploy the adapter and Server contract together, update all adapter fixtures to send an explicit classification, and test explicit `unknown` separately from an omitted legacy field.
- [A bot event with a supplied Slack `user` could be mistaken for a human by a future normalizer change] -> Keep bot-marker precedence in one normalization function and add cases for both `bot_id` and `bot_message` with and without `user`.
- [A refactor could move the Connection check below disabled auditing or another side effect] -> Keep the admission order documented at the route boundary and cover disabled, bound-thread, and existing-session bot cases with database no-side-effect assertions.
- [The Manager reduced DTO could silently lose the classification again] -> Treat `senderKind` as a required field of the adapter-to-Manager contract and assert the serialized HTTP request body in transport tests.
- [A successful Server response could be lost before Socket acknowledgement] -> Slack may retry, but the retry remains harmless because ignored processing performs no durable work. Server and adapter tests should cover repeated bot delivery and the normal error/unacknowledged path.
- [Making missing classifications unknown could affect non-adapter callers] -> Keep any compatibility fallback narrowly scoped to an omitted field, never to explicit `unknown`, and coordinate the release with direct HTTP test and caller updates.

## Migration Plan

1. Update adapter types and normalization tests, then ensure `senderKind` is forwarded by both connection and Manager transport requests.
2. Add the Server-side wire field and shared parsing, move Agent Connection bot admission before disabled auditing, and remove the Manager route's unconditional sender-ID requirement for bot-classified messages.
3. Add the Manager service early-return result and verify that both routes return `ignored` before inbox, claims, access checks, session processing, or outbox work.
4. Add the no-side-effects and human/unknown regression suites, including Manager App Bot and Agent App Bot messages, retries, disabled Connections, and bound sessions.
5. Deploy the adapter and Server contract as one coordinated release. No database migration or data backfill is needed. During rollout, stop or drain Socket adapters while the two components are updated so a new Server does not receive legacy envelopes and a new adapter does not send bot envelopes to the old Manager contract.
6. Verify adapter package tests and the focused Slack Server specs, followed by the repository's normal fast and full verification gates.

Rollback is a coordinated rollback of the adapter and Server binaries. Since this change adds no persisted values or schema, reverting before the new contract is in use is straightforward. If a mixed-version deployment is unavoidable, stop the adapter during the transition rather than allowing Manager bot events to be retried by an older route that still requires a sender ID.

## Open Questions

None remain for implementation. The wire values are lower-case `human`, `bot`, and `unknown`; bot admission is non-persistent and non-user-facing; and no migration is required.
