## Context

Issue 619 changes Slack ingress from a collection of independent rejection paths into an explicit admission and response-ownership flow. Today, `SlackConnectionRoutes` handles DMs and channel messages separately. New work can be rejected because the Agent is not executable or because the Connection is backpressured, but the response contract only communicates a loosely interpreted `kind`/`reason` pair. The adapter treats `backpressured` as permission to post directly, while most other rejections are written to the Slack outbox. This makes the boundary vulnerable to duplicate explanations when Slack redelivers an event or when two ingress requests race.

The existing persistence model already supplies the two identities needed for an idempotent implementation:

- `SlackMessageIdentity` is stable across redelivery: workspace, conversation, and message timestamp.
- `SlackProviderInboxRows` deduplicate accepted provider events, while `SlackOutboxRows` persist outbound delivery intents and reconcile uncertain Slack mutations.
- `SlackOutboxRows` already have a unique `(OwnerKind, ConnectionId, DispatchRef, Kind)` constraint and `EnqueueRequiredAsync` conflict handling.

The change must preserve the existing routing boundaries. Ordinary established DM and channel-thread sessions remain follow-ups, while the explicit DM `new task` marker retains its existing new-task launch meaning. Disabled Connections retain audited-discard behavior, executable Agents continue through the normal launch path, and `unknown` readiness remains accepted for Runner verification. Agent readiness rules themselves are not changing. `ConnectionDiagnostic` remains the authorized source for concrete setup, health, and next-action details; its diagnostic route must consume the same canonical execution result used by admission, including execution-history-based non-executable states and concrete gaps. Those details must not be copied into ordinary caller-facing Slack text.

The main stakeholders are Slack callers, Connection owners/operators, the Server ingress and outbox, and the Node/Go adapter transports. The implementation must also tolerate an adapter acknowledgment arriving after a durable outbox write, an ingress response being lost, concurrent requests for one event, and delivery uncertainty after Slack has accepted a message.

## Goals / Non-Goals

**Goals:**

- Classify an inbound event as established-session follow-up or new work before creating provider inbox or execution state; the explicit leading `new task` DM marker is new work even when a current DM Session mapping exists.
- Gate only new work: an ordinary DM without a current Session, an explicit leading `new task` DM even when a current Session exists, a channel root mention, or the first mention in an unbound thread.
- Block gated work before creating a `SlackProviderInboxRow`, Session, SessionInput, Turn, AgentJob, attachment execution state, or pending execution work.
- Persist one safe setup/unavailability nudge for a blocked event using a stable identity derived from the Connection and `SlackMessageIdentity`.
- Make ingress response ownership explicit so the adapter can distinguish Server-owned durable delivery from the legacy adapter-owned direct backpressure fallback.
- Preserve one response owner across redelivery, concurrent admission, outbox claim/retry, uncertain delivery, and reconciliation.
- Keep caller-facing text generic and actionable while retaining detailed diagnostics for authorized operators.
- Add Server, adapter, and wire-contract coverage for DM, channel-root, and unbound-thread behavior, including the no-duplicate boundary.

**Non-Goals:**

- Changing Agent readiness criteria, Runner verification, Agent execution, Session follow-up semantics, or automatic configuration repair.
- Creating setup guidance for Disabled Connections. Disabled ingress remains an audited discard with no outbox nudge.
- Changing the behavior of executable or `unknown`-readiness Agents.
- Adding new CLI/Web diagnostic surfaces or exposing credentials, internal exceptions, configuration values, or repair commands to Slack callers.
- Replacing the existing Slack inbox/outbox state machines or introducing a second delivery system.
- Making the legacy adapter-owned direct backpressure path durable. It remains the fallback used only when no durable response intent was created.

## Decisions

### 1. Put the new-work gate after routing classification and before persistence

Add a small Server-side admission service/policy used by both DM and channel ingress. The route will first perform the existing identity, managed-bot, access, mention, and binding checks. It will then classify the event:

1. Disabled and managed/ignored events follow their existing paths.
2. After prompt normalization, the explicit leading `new task` marker is classified as new work before DM mapping or the backpressure short-circuit is consulted. It therefore remains a new-work launch even when the DM has a current Session mapping. The known no-intent backpressure fallback may still own the response for this new-work path, but it must not bypass classification.
3. An ordinary DM with a current mapping, or a reply in an established channel thread, is a follow-up and bypasses the new-work readiness gate; its existing follow-up capacity/lifecycle behavior remains authoritative.
4. An ordinary DM without a current mapping, a channel root mention, or an unbound-thread first mention is new work and is evaluated by the gate.
5. Only an admitted new-work event proceeds to `SlackProviderInboxStore.AcceptAsync`, attachment binding, workspace provisioning, and `LaunchConnectionAsync`.

This ordering fixes the current DM problem where connection backpressure/readiness can be checked before determining that a message is a follow-up. It also moves the gate ahead of channel thread-history import, so a blocked launch does not perform unnecessary launch preparation or external history work.

For Agent state, admission will use the canonical `AgentReadinessService` result: `not-configured` and `not-executable` block, `executable` admits, and `unknown` admits. The authorized diagnostic route will call that same service and pass the lossless `AgentExecutabilityResult` into its response projection, alongside the existing structural readiness facts where needed for compatibility; it will not derive operator diagnostics from `AgentReadinessDeriver` alone. The diagnostic result will expose the canonical state, each concrete `AgentExecutabilityGap`, and each gap's `NextAction`. For Connection state, the service will reuse the existing diagnostic/admission vocabulary rather than duplicating health-string interpretation in each route. Disabled is handled separately; backpressure remains the legacy direct-fallback case; other non-ready enabled states such as incomplete setup, credential failure, or service unavailability become durable-nudge blocks. Owner access and identity-drift policy remain their existing routing decisions, not new execution-readiness rules.

**Alternative considered:** add another readiness check inside `IAgentLauncher` or Runner dispatch. That would be too late: it could already create Session and AgentJob state and cannot produce a Slack response with the required no-execution side effects. Patching each existing `if (IsBackpressured(...))` and readiness branch separately was also rejected because it would preserve change amplification and make DM/channel behavior diverge again.

### 2. Reuse the Slack outbox for durable nudges

A blocked new-work event will create a `SlackOutboxRows` entry with the existing `UserAction` kind and `EnqueueRequiredAsync`. No new table or outbox kind is needed. The nudge draft will contain:

- the originating project and Connection;
- the originating workspace and conversation;
- a reply anchor equal to the inbound `ThreadTs` when present, or the channel root message timestamp when the product chooses to reply in the root's thread; DMs without a thread remain top-level DM replies;
- a safe `SlackDeliveryPayload` using `post_message`;
- a stable `DispatchRef` generated by a shared helper from `ConnectionId` and the workspace/conversation/message identity.

The identity helper should hash a canonical string such as `connectionId + workspaceTeamId + conversationId + messageTs` before adding a short `slack-admission-nudge:` prefix. Hashing keeps the value below the existing 256-character column limit while preserving deterministic identity. The same dispatch reference is used as the payload `clientMessageId`, allowing delivery reconciliation to find a provider message after a lost Slack response.

`EnqueueRequiredAsync` is the correct persistence boundary because it commits before the ingress result is returned, treats the dispatch reference as unique, and resolves a uniqueness race to the existing row. A redelivery or concurrent request therefore returns Server-owned delivery for the same intent rather than creating another nudge. The nudge is not written to the provider inbox, so it cannot accidentally become execution work.

**Alternative considered:** add a separate `SlackAdmissionNudge` table and transactionally link it to the outbox. That would create a second source of truth for delivery ownership and require additional cleanup/reconciliation rules. The existing outbox uniqueness and state machine already provide the needed durable boundary.

### 3. Separate response ownership from the ingress kind

Extend the Server-to-adapter ingress result with an explicit `responseOwner` field:

- `none`: no user-visible response is expected from this ingress result, such as accepted work or an ignored event;
- `server`: Server committed a durable response intent, including the new setup/unavailability nudge and existing outbox-backed rejection responses;
- `adapter`: no durable response intent exists and the adapter must perform the legacy direct response.

The result continues to carry a `kind` for routing/observability and a safe `reason` when the adapter owns a response. A durable setup block may retain an internal result kind such as `agent_not_configured`, but its caller-visible text is the generic safe summary, not the current detailed readiness explanation. The Node contract in `packages/mohist-slack/src/types.ts`, the adapter transport, and the Go transport mirror in `packages/go/mohist-slack/serverapi.go` will all carry the field.

The adapter will stop inferring ownership from `kind === 'backpressured'`. It will post directly only for `responseOwner === 'adapter'`, using the supplied safe message and the original conversation/thread. For `responseOwner === 'server'`, it posts nothing and relies on normal outbox draining. For `none`, it also posts nothing. A valid Server-owned result is acknowledged immediately; an adapter-owned result is acknowledged only after the direct post succeeds; an error or malformed result before ownership is known remains unacknowledged so Slack can redeliver it.

The existing direct backpressure response will explicitly return `responseOwner: 'adapter'`. All outbox-backed rejection paths will return `responseOwner: 'server'`, which makes the no-duplication rule explicit for future rejection kinds as well.

**Alternative considered:** introduce a new `kind` for every response mode and retain adapter branching on kind. This would encode ownership indirectly, require more compatibility cases, and make future server-owned response kinds easy to mishandle. Ownership is an independent concern and should be represented as such.

### 4. Use safe public summaries and preserve diagnostics separately

The admission service will return an internal block category and a public summary. It will also use the canonical `AgentExecutabilityResult` returned by `AgentReadinessService`, rather than the structural `AgentReadinessDeriver` projection alone. The authorized diagnostic route will inject `AgentReadinessService`, call `GetAsync(projectId, agent, ct)`, and pass the lossless result into `ConnectionDiagnostic` through an explicit diagnostic-input field. `ConnectionDiagnosticResult` will add an authorized `AgentExecutability` projection containing the canonical state, concrete gaps, gap next actions, and their existing authorized fix-entry-point details; the existing Connection facts and Connection `PrimaryState`/`NextAction` remain unchanged. A lossy `ready`/`needs_setup`/`unknown` string cannot be the only readiness field. This makes a history-based `not-executable` admission block visible to Owners and authorized operators while keeping the caller summary safe.

The public summaries will be fixed, independent of the concrete failure:

- Agent/setup block: indicate that the Agent is not ready to accept new work and ask the caller to contact the Connection owner or finish setup through the normal owner workflow.
- Connection availability block: indicate temporary unavailability and suggest retrying shortly or contacting the Connection owner.
- Adapter-owned backpressure fallback: retain a generic retry/backpressure message without queue counts or internal health details.

No configuration property, credential, token, exception text, shell command, or repair instruction will be interpolated into the Slack payload. Operators continue to use `GET /api/projects/{projectRef}/slack-connections/{connectionId}/diagnostic` and existing authorized surfaces to see the canonical executability state, concrete execution gaps, Connection health reason, and next action. The endpoint must derive readiness through the canonical execution service on every diagnostic request and serialize its `AgentExecutability` projection; it must not continue to rely only on `AgentReadinessDeriver`. Internal logs/telemetry may record the block category, but must use the existing secret-redaction conventions.

**Alternative considered:** send the current `AgentConnectionDispatchDecision.Reason` to Slack. This would leak implementation and repair details and would make caller text vary with internal readiness implementation. The current diagnostic endpoint is already the correct information boundary.

### 5. Preserve durable ownership through uncertain delivery

The nudge payload will use the existing outbox delivery lifecycle. The adapter's normal drain will claim the row and post it with the stable `client_msg_id`; it will acknowledge the original row as delivered with the provider message identity. If the provider response is lost, the row enters `delivery_uncertain`. The existing uncertain-claim path will call `reconcile` before any resend:

- find the message by provider identity or stable `client_msg_id`;
- mark the original row delivered when the message exists;
- retry the original row with the same dispatch reference only after absence is confirmed.

Ingress redelivery, adapter reconnect, outbox sweeps, and reconciliation must never turn a Server-owned nudge into an adapter-owned direct message. The durable row is the authority once it exists. The direct fallback is returned only from a path that did not create a durable intent, such as the pre-existing backpressure branch or an explicitly handled no-intent capacity failure.

**Alternative considered:** post the nudge directly from the adapter after a Server block and let the outbox carry only later Agent replies. That recreates the lost-response and duplicate-post race the change is intended to remove.

### 6. Handle persistence failure without creating execution work

The nudge helper will distinguish expected no-intent backpressure/capacity outcomes from unknown Server failures. When the existing direct fallback is applicable and no outbox row was committed, the Server returns `responseOwner: 'adapter'` with the safe direct text. It must return before provider inbox acceptance and before any launch side effect. For an unexpected database or infrastructure error where ownership cannot be known, the route should fail the ingress request rather than claim a direct response; the adapter will leave the Slack event unacknowledged and Slack can redeliver using the same identity.

This avoids hiding a persistence outage as a successful direct response while still preserving the legacy behavior for the known backpressure case.

### 7. Test the ownership boundary as a wire contract

Coverage will be added at three levels:

- Server spec tests for unconfigured/non-executable Agents, unavailable Connections, existing DM follow-ups, explicit `new task` DMs, channel roots, and unbound threads. Assertions will verify the response owner, exactly one outbox row when durable, correct conversation/thread anchor, and zero provider inbox/session/input/turn/job rows for blocked new work.
- The authorized diagnostic endpoint will be exercised with both structural setup gaps and execution-history-based `not-executable` results. Tests will verify that the response exposes the canonical executability state, concrete gap, gap next action, Connection state, and Connection next action, while the caller-facing nudge remains free of those details.
- Concurrency and redelivery tests using the same `(ConnectionId, workspace, conversation, messageTs)` will assert one dispatch reference and one outbox row, with all successful responses reporting Server ownership.
- Adapter tests will feed both ownership results through the real event handler: Server-owned results must not post directly and must acknowledge once; adapter-owned results must post once and acknowledge only after success; unknown or malformed results must not acknowledge. Delivery tests will cover uncertain reconciliation by stable `client_msg_id`.

The Server integration fixture and adapter contract fixture should share representative JSON payloads so the breaking internal contract is tested from both sides; those fixture tests are wire-level coverage only. A separate cross-component harness will invoke the actual Server ingress HTTP route with a blocked event, feed that response to the actual Node adapter event handler, and use instrumented Slack post/ack and outbox-drain seams to assert the durable-outbox/direct-send no-duplication boundary end to end. Existing manager and non-admission ingress results should default to `none` or `server` according to whether they create an outbox response and must not acquire new direct-send behavior.

## Risks / Trade-offs

- **[A Connection becomes Disabled after a nudge is committed but before delivery]** -> Preserve the existing outbox rule that disabled Connections are not claimed. The nudge remains durable and can be delivered after re-enable; no new Disabled delivery semantics are introduced.
- **[A durable nudge is committed but the HTTP response is lost]** -> Redelivery recomputes the same dispatch reference and resolves the existing outbox row, returning Server ownership without creating another intent.
- **[Two ingress requests race before either sees the outbox row]** -> Use the existing unique dispatch-reference constraint and `EnqueueRequiredAsync` conflict resolution as the ownership boundary; never rely on a pre-flight read alone.
- **[Slack accepts a nudge but the adapter loses the provider response]** -> Use the stable `client_msg_id` and existing uncertain-delivery reconciliation before retrying the same row.
- **[The direct adapter fallback is itself uncertain]** -> Keep it limited to the legacy no-intent backpressure path and preserve the existing acknowledgment-after-post rule. The durable nudge path, which is the required deduplicated path, never falls back to a second direct post after ownership is Server-side.
- **[A readiness or health reason is accidentally exposed through a new response path]** -> Centralize public-summary mapping in the admission service, add forbidden-content assertions for credentials/errors/commands, and keep diagnostic facts in the authorized diagnostic endpoint only.
- **[DM or channel follow-up is accidentally classified as new work]** -> Resolve ordinary DM session mappings and channel thread bindings before applying the gate, but classify the explicit leading `new task` marker as new work first; add regression tests for both an ordinary established-session follow-up and a marked new task after readiness changes.
- **[Stable dispatch references exceed the existing column limit or collide across contexts]** -> Canonicalize all identity components with explicit separators and use a fixed-length cryptographic digest with a namespaced prefix; test same message across different Connections and conversations.

## Migration Plan

1. Add the shared response-ownership model and tolerant transport decoding to the Node and Go adapters, retaining a compatibility fallback for an older Server response: an omitted owner on `backpressured` is treated as adapter-owned, while other omitted-owner results retain the current no-direct-post behavior.
2. Deploy the Server admission classifier and durable nudge writer. It uses existing `SlackOutboxRows`, `UserAction`, state transitions, and unique indexes, so no database migration is required.
3. Deploy the adapter ownership handling and adapter/server contract tests. Once both sides are available, enable the new gate for all Slack Connection ingress; no per-Connection data migration is needed.
4. Monitor outbox states, `responseOwner` outcomes, duplicate dispatch references, delivery-uncertain rows, and blocked-event counts through existing logs/diagnostic tooling. Confirm that blocked events have no provider inbox or execution records.

Rollback is code-based. Revert the Server admission/ownership implementation and then the adapter if necessary. Do not delete pending nudge rows during rollback: the existing outbox drain can safely deliver them, and removing them would create a lost-response window. With no schema change, rollback does not require a database downgrade. If the adapter is rolled back first, Server-owned responses remain durable and the older adapter will acknowledge non-backpressure rejections without sending a second direct message; the legacy `backpressured` result remains directly rendered.

## Open Questions

- Should a blocked channel root mention receive the nudge as a reply in a new thread anchored at the root message, or as a top-level channel message? The design uses the root timestamp as the anchor when thread-style guidance is desired; this should be confirmed against the existing Slack conversation UX.
- Which enabled Connection states beyond explicit backpressure are considered admission-unavailable for this issue? The proposed mapping blocks incomplete setup, credential failure, and service-offline states, while leaving owner-unavailable and identity-drift behavior to their existing policies.
- What exact caller wording and owner next step should be standardized, and should the message include a safe link to the existing Connection diagnostic page? The link must not bypass authorization or expose diagnostic content to ordinary callers.
- Is the Go adapter transport still a released runtime path for this contract, or only a maintained port? The design keeps it wire-compatible unless the repository owners explicitly retire it.
