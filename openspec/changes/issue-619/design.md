## Context

Issue 619 adds setup-mode guidance at the Server Slack ingress boundary. The current ingress in `SlackConnectionRoutes` already checks Agent executability, but it reduces the canonical `AgentReadinessService` result to a state-only decision and posts a generic reply without a stable delivery reference. It also lets new work reach the Agent launch path when the non-disabled Slack Connection is unavailable. The result is neither actionable for the caller nor deduplicated when Slack redelivers an event.

`AgentReadinessService.GetAsync` is the authoritative source for `not-configured`, `not-executable`, `unknown`, and `executable`, including canonical gaps, next actions, and repair entry points. `AgentQuerier` already hydrates this result onto `AgentInfo.Executability` for privileged Agent surfaces. Slack provider inbox and outbox state are Server-owned; the adapter is stateless and already supports durable `post_message` delivery through `SlackOutboxStore`.

The change affects new-work paths: a DM that resolves to a new launch, including an explicit `new task`, and a channel mention that would create a new Session, either at the channel root or as the first mention for this Connection in an unbound thread. Existing DM and channel-thread follow-ups must continue using their existing Sessions, even if the Agent definition later becomes blocked. Connection setup, desired state, health, and backpressure remain independent of Agent executability.

## Goals / Non-Goals

**Goals:**

- Evaluate the canonical `AgentExecutabilityResult` and the current Connection availability before admitting a new Slack launch.
- Preserve distinct `agent_not_configured` and `agent_not_executable` admission codes when the Agent gate is the blocking cause, and preserve the existing `backpressured` response code when backpressure is the Connection gate.
- Post one Server-authored setup/unavailability nudge in the triggering conversation, at the triggering thread or root target.
- Keep caller-visible nudge text to a safe setup/unavailability summary; retain Agent gap details and Connection health/repair details only in authorized Agent and operator surfaces.
- Deduplicate concurrent and redelivered nudges for either blocking cause using `(ConnectionId, WorkspaceTeamId, ConversationId, MessageTs)`.
- Ensure blocked new launches create no provider inbox work, workspace, AgentJob, AgentSession, SessionInput, or AgentTurn.
- Preserve the existing Disabled audited-discard behavior, current executable and unknown readiness behavior on an available Connection, existing-session follow-ups, outbox retries, delivery uncertainty, and reconciliation.

**Non-Goals:**

- No second readiness calculation in the Slack adapter or Slack route layer.
- No change to Agent execution snapshots or existing Session history.
- No change to Connection lifecycle or health state because of Agent readiness.
- No Agent-generated reply, progress projection, reaction, or accepted-task status for a blocked launch.
- No new Slack command grammar, interactive setup flow, external dependency, or adapter-owned persistence.
- No automatic repair of Agent configuration from Slack.

## Decisions

1. **Carry the full canonical readiness result through Slack admission.**

   Replace the state-only shape returned by `ResolveInboundDispatchDecisionAsync` with a decision that retains the `AgentExecutabilityResult` and maps its state to the existing admission code. The safe nudge renderer consumes only the blocking category and fixed, non-sensitive copy. Authorized Agent/operator surfaces continue to expose the full result, including every gap's code, message, next action, and `FixEntryPoint`, from `AgentInfo.Executability` and launch rejection payloads. The Slack Connection diagnostic should include the same canonical executability result alongside its existing Connection facts, including health, setup progress, backpressure, and offline-gap facts, while retaining the legacy Connection `AgentReadiness` field for compatibility.

   Alternatives considered: deriving readiness from `AgentConfig` in Slack was rejected because it misses execution evidence and duplicates the canonical rule; returning only the error code was rejected because it loses privileged diagnostic information and makes the Slack boundary choose a second representation of the result.

2. **Gate only new launches, after classification and before admission side effects.**

   DM ingress will resolve the current DM mapping before either gate. A route classified as `Followup` continues through the existing Session path; `Launch` and `NewTaskLaunch` are checked for Connection availability and then canonical Agent executability. Channel ingress keeps access, binding/reconciliation, and message classification first. A root mention or a first mention for this Connection in an unbound channel thread is a new launch and is checked by both gates; a bound-thread follow-up skips both. A read-only lookup of the stable message identity may be used to recognize a previously accepted replay before applying the new-launch gates; an accepted replay reuses its existing inbox, reservation, or Session route rather than producing a new nudge.

   For a new blocked launch, the sequence is: validate transport identity and access, validate task content, classify the route and resolve any accepted replay, evaluate the Connection availability gate, resolve the Agent and evaluate canonical executability when the Connection is available, enqueue the setup/unavailability nudge, and return the applicable admission result. The Connection gate applies to an enabled Connection whose `SetupProgress` is not complete, whose `ConnectionHealth` is `Unhealthy` or `Degraded` (including service-offline and backpressure reasons), or whose `OfflineGapAt` is set. `DesiredState == Disabled` is excluded and continues through the current audited-discard branch. The implementation must not call `SlackProviderInboxStore.AcceptAsync`, read thread history for launch context, prepare attachments, provision a workspace, call `IAgentLauncher`, create a Session grain input, or enqueue liveness status in a blocked branch. Executable and unknown results continue through the existing inbox and launch flow unchanged when the Connection is available.

   A non-disabled unavailable Connection retains the existing Connection-specific admission code where one exists (`backpressured` for backpressure); other unavailable states use a distinct `connection_unavailable` result. All unavailable branches use the same required nudge intent and create no executable work. `EnqueueRequiredAsync` is allowed to insert the required nudge even when the pending outbox count has crossed the backpressure threshold; if persistence itself fails, the request fails with the existing transport/persistence error and still creates no executable work.

   Alternatives considered: checking readiness before DM route classification would also block existing-session follow-ups and violate the persisted-snapshot behavior; checking after inbox acceptance would leave queued provider work for a launch that must be rejected.

3. **Represent setup and Connection-unavailability guidance as a required existing outbox UserAction.**

   Add a Server-side nudge helper that calls `SlackOutboxStore.EnqueueRequiredAsync` with a `SlackOutboxDraft` whose operation is `post_message`, kind is `SlackOutboxKinds.UserAction`, and payload contains only safe text and the stable client message id. Do not use `EnqueueAgentReplyAsync`, terminal status projection, or a progress row: the nudge is authored by Mohist and is not an Agent answer or accepted work. The same safe summary can cover both blocking categories, while the admission result and privileged diagnostic retain the category.

   Derive the dispatch reference deterministically as `slack-setup-nudge:{connectionId}:{identity.AsKey()}`. Set `ConversationId` and `WorkspaceTeamId` from the triggering identity. For a DM, preserve `body.ThreadTs` when present; for a channel-root launch, leave the thread target unset; for a first mention in an unbound channel thread, set the thread target to `body.ThreadTs`. The safe text states that the Agent or Slack Connection is not ready to accept the task and directs the caller to the responsible owner/operator, without gap codes, detailed health/failure text, configuration, credentials, repair paths, or commands.

   The existing required-delivery unique index on owner, Connection, dispatch reference, and kind makes the operation race-safe. `EnqueueRequiredAsync` returns the existing row on a duplicate, including when the first row is delivered, uncertain, or pending, so every admission observes one logical intent. Existing adapter `post_message` handling, retry, delivery-uncertain, reconciliation, and manual resend behavior remain applicable.

   Alternatives considered: adding a new outbox kind was rejected because it would require a database check-constraint migration and adapter/operator handling for no behavioral gain; a process-local cache or inbox row was rejected because neither survives restart and an inbox row would violate the blocked-admission no-work contract.

4. **Keep privileged readiness and Connection state separate.**

   `AgentReadinessService` remains the only Agent readiness authority. The ingress must not use the persisted `AgentConnection.AgentReadiness` or `AgentReadinessDeriver` as an Agent launch verdict. For a new launch, the Connection availability gate has precedence over the Agent gate and uses the current Connection response taxonomy; it never becomes an Agent setup gap. `DesiredState == Disabled` retains its current audited-discard behavior. Conversely, a blocked Agent result or an unavailable Connection does not mutate `DesiredState`, `SetupProgress`, `ConnectionHealth`, or `OfflineGapAt`.

   The diagnostic/read APIs should expose canonical executability as a separate field or nested object, while their primary Connection state continues to describe Slack setup and transport facts. This lets an owner/operator inspect exact Agent gaps, Connection health/offline/backpressure facts, and repair entry points without exposing them in a Slack message.

5. **Test the resource boundary and delivery convergence as separate contracts.**

   Extend the Slack DM and channel ingress specs for Agent-blocked and non-disabled Connection-unavailable launches. Assert the admission code, safe payload, conversation/thread target for DM, channel root, and unbound channel thread, one outbox row after sequential redelivery, one row under concurrent admission, and unchanged counts for inbox, jobs, sessions, inputs, turns, attachments, and existing Session snapshots. Add an uncertain-delivery case for each blocking category that retries or reconciles the same row without inserting another one.

   Add coverage that executable Agents follow the normal launch path on an available Connection, unknown Agents remain admitted for Runner verification, existing DM/thread follow-ups remain usable while readiness or Connection state is blocked, and Disabled remains audited-discarded without a nudge. Cover unhealthy/service-offline, offline-gap, setup-incomplete, and backpressured Connection states as unavailable nudge cases, and assert those states are not reported as Agent readiness gaps. Add API/read-surface assertions for the complete canonical state and Connection facts, including next actions and repair entry points, while asserting those details do not occur in the nudge payload.

## Risks / Trade-offs

- `[Risk]` A Slack API result can remain delivery-uncertain, so the caller may see no nudge or may see a duplicate after a manual resend. -> `Mitigation`: retain one durable outbox row and use the existing uncertainty/reconciliation warning; never create a replacement intent automatically.
- `[Risk]` Required UserAction delivery can encounter outbox capacity pressure. -> `Mitigation`: use `EnqueueRequiredAsync`, which preserves required rows even after the pending threshold and marks the Connection backpressured; return the existing transport/persistence error only if the required row cannot be persisted, with no executable work created.
- `[Risk]` A rolling deployment with old Server instances can still emit the legacy generic reply. -> `Mitigation`: deploy the ingress change across all Server instances before resuming adapter traffic; the existing outbox schema remains compatible, and rollback must accept that already-created nudges cannot be unsent.
- `[Risk]` Canonical readiness is calculated at admission time and can change between redelivery attempts. -> `Mitigation`: distinguish previously accepted identities through durable replay lookup, keep stable nudge identity for blocked attempts, and never alter existing Session snapshots or accepted execution records.
- `[Risk]` Reusing `UserAction` makes setup nudges share the broad outbox kind with other user-visible actions. -> `Mitigation`: use the dedicated `slack-setup-nudge:` dispatch-ref namespace and stable payload client id for operator inspection and future filtering without adding a new persistence kind.
- `[Risk]` A safe summary can be too vague for a caller to act on. -> `Mitigation`: direct the caller explicitly to the bound Agent owner/operator, while exposing the precise canonical next action and repair entry point through authorized surfaces.

## Migration Plan

1. Implement the readiness-result plumbing, launch-only gate, stable nudge helper, privileged read-surface projection, and focused Server specs. Update Slack documentation to describe the setup-mode response and the separation between caller-safe and operator-only detail.
2. Deploy the Server code with the existing Slack outbox schema. No database migration is required because the implementation uses the existing `UserAction` kind, `post_message` operation, and required-delivery uniqueness constraint.
3. Roll out all ingress-serving Server instances before re-enabling or relying on adapter delivery. Existing pending outbox rows remain valid; new setup nudges are picked up by the unchanged adapter claim and acknowledgement protocol.
4. Verify Agent-blocked and non-disabled Connection-unavailable DM events, root and unbound-thread mentions, Disabled audit behavior, redelivery/concurrency deduplication, and delivery-uncertain recovery in staging before enabling the workflow broadly.
5. Rollback is a code rollback only. Leave existing setup-nudge rows in the outbox so already-persisted intents can finish or be reconciled. A rollback cannot retract a nudge already sent and may restore the old generic response for new blocked events; redeploy the new Server version to regain the stable setup-nudge contract.

## Open Questions

- Confirm the final localized wording of the single safe nudge for `not-configured` versus `not-executable`; the technical contract intentionally permits the same safe setup direction for both states.
- Decide whether the operator delivery list should render the `slack-setup-nudge:` namespace as a named "Agent setup nudge" label or continue showing it as an ordinary `UserAction`; this is observability-only and does not affect delivery semantics.
