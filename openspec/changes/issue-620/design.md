## Context

Issue 620 extends the existing Slack control path with two interactive actions: retrying a retryable failed execution and choosing one Bot when a message mentions multiple eligible Bots. The proposal and capability specs require both actions to be signed by the Server, bound to the receiving Slack Connection and actor, short-lived, and rejected without runtime side effects when stale or replayed.

The current implementation already provides the required transport boundaries:

- `SlackTurnControlService` creates and verifies HMAC-signed Stop actions using the Connection's verified Bot token, expiry, actor/context checks, and constant-time comparison.
- `SlackInteractionRoutes` validates the adapter lease, loads the Connection, delegates action authorization to the Server, and persists the resulting `chat.update` through `SlackOutboxStore`.
- `SlackConnectionRoutes` uses stable Slack message identities, `SlackProviderInboxStore`, `IAgentLauncher`, session follow-up dispatch, thread mappings, and `SlackStatusProjection` for durable ingress and status delivery.
- `SlackAmbiguousPromptStore` currently claims one prompt per `(workspace, conversation, message timestamp)` and stores only the first prompt owner and mentioned Connection IDs. It does not yet store the original message, actor, signed actions, or a selection outcome.
- `mohist-slack` acknowledges `block_actions` promptly, forwards the normalized action ID/value and Slack context, and already delivers Server-provided Block Kit blocks through the outbox contract.

The main architectural gap is durable arbitration. A valid click must commit its operation identity before any session or turn dispatch, and concurrent clicks must converge on one recorded result. The second gap is terminal failure presentation: failure deliveries currently finalize liveness, but do not carry a signed Retry control or enough explicit turn identity for deterministic retry authorization.

## Goals / Non-Goals

**Goals:**

- Add signed, actor-bound, context-bound Retry and Bot-selection actions without weakening the existing Stop action.
- Make Retry and selection idempotent across Slack redelivery, adapter failover, concurrent clicks, and a lost response.
- Reuse the existing session, launch, inbox, thread-mapping, and outbox boundaries for actual execution and presentation.
- Persist the complete ambiguous-message source and candidate set so selection never trusts redelivered Slack text or candidate identifiers.
- Ensure a failed retryable result exposes exactly one Retry control, while accepted, stale, rejected, and replayed actions replace the obsolete controls with readable status.
- Route an ambiguous original message to exactly one selected Connection in its original conversation and thread.
- Preserve adapter statelessness and introduce no third-party dependency.

**Non-Goals:**

- Changing Agent Runtime retry semantics, turn state transitions, or the existing Stop operation.
- Adding free-text action payloads, local adapter authorization, automatic Bot selection, or fallback inference from a user's later text.
- Retrying a running, completed, cancelled, unknown, superseded, or otherwise non-retryable turn.
- Broadcasting one Slack message to multiple Connections.
- Redesigning Manager Slack interactions or adding a public Slack HTTP ingress path.

## Decisions

### 1. Extend the existing Server-signed action boundary

Add separate action identifiers for Retry and Bot selection and extend `SlackTurnControlService` with shared signing and verification helpers. The common codec will keep the current `v1` JSON envelope and HMAC-SHA256 scheme:

- Retry payload: action, Connection, session, failed input/turn, dispatch reference, workspace, conversation, message, optional thread, bound actor, nonce, expiry, and signature.
- Selection payload: action, prompt identity, prompt-owner Connection, selected Connection, ordered candidate-set fingerprint, workspace, conversation, prompt message, optional thread, bound actor, nonce, expiry, and signature.

The candidate-set fingerprint binds the complete persisted set without putting an unbounded candidate document in every button value. The signed value contains no Slack credential. Canonicalization must include every authorization-relevant field and use `CryptographicOperations.FixedTimeEquals` for verification, as Stop does today.

`SlackInteractionRoutes` remains the authentication and delivery boundary. It validates the normalized `block_actions` envelope, delegates the action-specific decision to the Server, and enqueues the returned text and blocks with a stable result dispatch reference. It will no longer derive a separate result reference from every raw action value when the service can return the operation/prompt reference. This prevents concurrent selections from creating unrelated updates to the same prompt.

**Alternative considered:** Have the adapter verify signatures or choose the selected Bot. Rejected because it duplicates authorization logic, exposes Connection credentials to a transport concern, and allows failover adapters to disagree. The adapter continues to acknowledge and forward only.

**Alternative considered:** Use Slack's request signature as the action authorization mechanism. Rejected because it authenticates the Slack delivery, not the actor, target turn, candidate set, or single-use operation represented by the action.

### 2. Record Retry operations before dispatching a fresh attempt

Introduce a dedicated `SlackRetryOperations` persistence boundary rather than overloading `SlackProviderInboxRows`. The provider inbox records receipt of an original Slack message ingress; it does not retain an action operation outcome or provide recovery after a dispatch response is lost.

A Retry operation is keyed by a stable hash of the signed action value (the canonical payload plus its nonce) and stores the failed target identity, the original Slack provenance, a retry-specific dispatch key, pre-minted attempt identities, state, outcome, and resulting session/input/turn identifiers. The retry dispatch key is derived from the operation key, for example `slack-retry:{projectId}:{actionKey}`; it is never the original `slack:{workspace}:{conversation}:{message}` key. The unique action key and a conditional state transition provide the concurrency fence:

1. Verify the action and re-read the authoritative session/turn, terminal facts, retryability policy, and original Slack provenance.
2. Reject expired, unauthorized, wrong-context, non-failed, superseded, or unavailable targets before creating work. Expiry is checked here, before the operation is accepted.
3. Insert or load the operation. The first caller atomically claims the dispatchable state; replays load the same recorded outcome.
4. In the same durable operation record, commit the operation key, retry dispatch key, attempt identity plan, and `dispatch-pending` state before invoking any session or launch dispatcher.
5. Resume that operation with its stable key. If the caller dies after the commit, a later interaction replay or the recovery worker repeats the same idempotent dispatch rather than creating another attempt. Once committed, recovery does not reject the operation merely because the button has subsequently expired.
6. Persist the accepted/dispatched outcome and resulting identities, then enqueue the durable presentation using the operation's stable result reference. The outbox write is also replayable and conditional on the same reference.

The operation has two explicit launch boundaries. A failed root launch gets a new Session, initial input, and initial turn. `IAgentLauncher` gains a retry-specific connection-launch method that accepts the operation's explicit idempotency key and pre-minted Session/input/turn IDs, while retaining the original `ConnectionLaunchOrigin` (workspace, actor, conversation, source message, and thread) for provenance and Slack routing. This method calls the existing launch coordinator with the retry key, so it does not reuse the original message coordinator. A failed threaded turn gets a new follow-up input and turn in its existing Session through `AcceptFollowupAsync`, using the same retry key as the follow-up idempotency key, followed by `AgentSessionFollowupDispatcher`. The failed turn remains immutable in both cases. If a threaded Session cannot accept a follow-up, or the authoritative root source cannot be reconstructed, the operation returns `unavailable` without dispatch.

The retry source is copied from authoritative session/input state, including normalized prompt, provenance, and accepted attachments; action-submitted text is never used. Root retry status and reply delivery continue to target the original Slack message/thread even though the new attempt has new Session/input/turn identities. The provider inbox is used for the original message ingress before launch or prompt creation; a `block_actions` request does not call `SlackProviderInboxStore` because its `(ConnectionId, workspace, conversation, message)` key represents the source message and would incorrectly treat a button click as the already-processed message. The action operation is the durable receipt for the click. A duplicate interaction therefore loads and resumes the operation rather than returning only an inbox duplicate.

An accepted Retry update acknowledges the new attempt and projects working state. It rebuilds the existing signed Stop control for that new turn when applicable. Every rejected, stale, unavailable, already-applied, or replayed result removes the Retry action from the rendered blocks. Stable outbox references make the result update idempotent even when Slack delivery is uncertain.

**Alternative considered:** Reuse the failed turn or mutate its status to represent the retry. Rejected because it destroys historical failure evidence, makes concurrent retry checks ambiguous, and violates the requirement that the fresh attempt have a new identity.

**Alternative considered:** Use only `SlackProviderInboxStore` as the operation log. Rejected because an accepted inbox row has no durable retry result, dispatch key, or recovery state, so a crash between acceptance and runtime dispatch cannot be resolved deterministically.

### 3. Enrich terminal failure delivery with authoritative retry context

Extend the Server-owned terminal delivery contract for Connection-owned Slack work with explicit session, input, turn, and retryability facts. Initial-launch terminal events can source these identities from the AgentJob's persisted launch plan; follow-up terminal events should include the session/input/turn identity when emitted by `AgentSessionGrain`. Legacy events that lack the identities remain renderable but cannot expose Retry.

Retryability is a deterministic Server policy, not an open-ended interpretation of failure text. The classifier requires terminal status `failed` and an authoritative, normalized category in this exact allowlist: `runner-unavailable`, `runner-lost`, `report-timeout`, `timeout`, `deadline-exceeded`, `probe_timeout`, `opencode-transport-failed`, `unavailable-runtime`, `rate_limited`, or `retry-safe`. It compares categories case-insensitively after trimming, but does not normalize arbitrary punctuation or infer from the failure message. Completed, cancelled, unknown, category-less, legacy-without-category, unrecognized, input, permission, configuration, context, missing-session, and generic `turn-failed` failures are non-retryable. The same policy applies to initial launches and follow-up turns; the category-to-control matrix is part of the terminal-rendering tests.

For a retryable failed Connection result, `SlackTerminalDeliveryHandler` will:

- render the existing sanitized failure evidence as readable Server-owned failure text;
- ask `SlackTurnControlService` to create one signed Retry action after rechecking the authoritative failed turn and original provenance;
- pass the failure text and Retry blocks through `SlackStatusProjection`/`SlackOutboxStore` as a replaceable terminal presentation.

Completed and cancelled results do not receive Retry. Successful Agent-authored reply behavior remains unchanged; the new Server-authored projection is limited to the failure presentation that owns the recovery control. The projection uses the existing provider-message identity and `chat.update` promotion behavior so it does not append duplicate status messages.

**Alternative considered:** Generate Retry from the failure text or from the Agent output. Rejected because text is not an authoritative work identity and may contain untrusted or redacted content. Retry creation must use persisted session/turn facts.

### 4. Turn the ambiguous prompt row into a single-winner selection record

Evolve `SlackAmbiguousPromptStore` and `SlackAmbiguousPromptRow` while preserving the existing unique key `(WorkspaceTeamId, ConversationId, MessageTs)`. In addition to the current prompt owner and candidate IDs, persist:

- the original actor and normalized source message, including bounded text and accepted attachment descriptors;
- the original thread identity and prompt delivery reference;
- stable prompt nonce and expiry;
- an ordered candidate descriptor set containing Connection IDs and display labels;
- selection state, selected Connection, selection operation identity, and dispatch/result identifiers.

The first-writer claim remains the prompt deduplication fence. Candidate computation must be deterministic from the current workspace's enabled, identity-bound mentioned Connections and the sender's access decisions. A duplicate ingress may recreate the one required outbox delivery, but may not replace the stored candidate set or source message.

The prompt owner Connection signs one stable action value per candidate. The action value is generated from persisted nonce/expiry and candidate-set fingerprint, so redelivery recreates the same controls. The prompt includes one button per persisted eligible candidate and readable fallback text instructing the user to explicitly mention one Bot. If the prompt was delivered as a separate Bot message, selection authorization also checks that the interaction's provider message identity matches the prompt delivery identity recorded by the outbox; the original human message identity remains the durable routing key.

On selection, the Server verifies the signature and prompt delivery context, loads the row, compares the signed candidate fingerprint, and rechecks workspace, prompt-owner Connection, actor access, selected Connection eligibility, and expiry. A transaction conditionally records the first winner and stable selection operation before dispatch. A losing concurrent candidate observes the winner and returns `already_applied` or `stale` without dispatch.

Dispatch uses the authoritative source stored in the prompt. A root message uses the selected Connection's normal channel launch path with the original Slack identity and its existing stable launch key. A threaded message uses the selected Connection's existing thread session follow-up path when available; otherwise it uses the normal thread launch path. The selected Connection alone owns subsequent status and reply delivery. The selection update targets the prompt's Bot message, replaces all choice actions, and names the selected Bot. If the selected Connection became disabled, unbound, unauthorized, or otherwise unavailable, the row records `unavailable` and the original message is not silently rerouted.

**Alternative considered:** Keep only the first prompt owner and mention IDs, then trust the clicked candidate value. Rejected because the value can be tampered with and the current row cannot reject stale candidates or prove which original message should be dispatched.

**Alternative considered:** Resolve the original message again from Slack on click. Rejected because Slack redelivery text and current channel history are not the durable source accepted by Mohist, and an external read would introduce another failure and race path.

### 5. Keep Block Kit and adapter behavior as a pass-through contract

No new adapter-side routing model is required. `SlackInteractionEnvelope` already carries the stable interaction identity, action ID, signed value, actor, workspace, conversation, message, and thread. The adapter changes are limited to contract tests and any type/validation adjustments needed to preserve the new action IDs and values unchanged.

`SlackDeliveryPayload.Blocks` remains an opaque Server-owned Block Kit array. Existing `post_message`, `chat_update`, provider-identity reconciliation, uncertain delivery handling, and outbox acknowledgement are reused. The adapter acknowledges Slack before Server processing and never interprets button labels, candidate IDs, signatures, or result states.

**Alternative considered:** Add a separate adapter endpoint for each action. Rejected because both actions are already represented by the existing `block_actions` envelope and separate routes would duplicate lease, acknowledgement, and delivery handling.

### 6. Verify the state machines at the Server and adapter boundaries

Add Server tests for signing canonicalization, credential omission, expiry, actor/context authorization, terminal-state revalidation, retry operation concurrency/replay, fresh root Session and threaded follow-up identity creation, single-winner prompt selection, candidate invalidation, original-context routing, and durable presentation updates. Add migration/store tests for prompt and operation uniqueness, the retryability category matrix, conditional claims, operation recovery after a write-before-dispatch failure, and selection recovery after the winner is committed but before dispatch. Inject failures at each boundary: after the operation commit, after launch/follow-up idempotent admission, before the operation outcome update, and before the interaction response. Verify that a process restart or interaction redelivery re-enters the pending operation and produces at most one attempt.

Extend `mohist-slack` tests to prove prompt acknowledgment precedes forwarding, action IDs/values and Slack identity are preserved, raw payloads and credentials are omitted, and Server-provided Retry/selection blocks reach `chat.update` unchanged. Existing Stop tests must remain green.

### 7. Re-enter committed actions after process loss

Add a fixed-key `SlackActionRecoveryGrain` with a persistent Orleans reminder, following the existing Slack outbox dispatcher pattern. Its scoped `SlackActionRecoveryService` periodically claims due `SlackRetryOperations` rows in `dispatch-pending` and ambiguous prompt rows in `selection-dispatch-pending` using a short conditional recovery lease, then calls the same Retry or Selection operation resume method used by the interaction route. A completed or unavailable row is terminal and is skipped; a lease-expired pending row is eligible again. The worker is registered in the normal Server service graph, and the reminder is the liveness mechanism after process restart; an immediate nudge after an interaction commit only reduces latency.

The interaction route follows this order: validate the adapter lease and normalized envelope, verify the signed action and current authorization, atomically claim or load the action operation, commit its pending state before runtime dispatch, invoke the operation resume method, persist the outcome, and enqueue the stable result presentation. A redelivery that finds an existing receipt must still call resume: it returns the recorded outcome or advances a pending operation, and `SlackInteractionRoutes` always asks the outbox to enqueue the returned presentation reference. Outbox uniqueness collapses duplicate updates. Original message ingress keeps its existing order of provider-inbox acceptance before launch/prompt work; button clicks use the action operation as their separate receipt and must never be stopped by the source-message inbox's duplicate result.

## Risks / Trade-offs

- [A Bot token rotation invalidates already-rendered action values] -> Treat the action as invalid with no runtime side effect; the short expiry bounds the stale-control window and the failure remains readable.
- [A process crash after an operation commit but before runtime dispatch can leave work apparently accepted] -> Store a stable dispatch key and pre-minted attempt identities before dispatch, make the root launch coordinator and threaded follow-up accept idempotent on that key, and use the fixed-key `SlackActionRecoveryGrain` reminder plus interaction redelivery to resume the same pending operation.
- [The current terminal event shape does not identify every failed follow-up turn] -> Enrich initial and follow-up terminal events; legacy events render without Retry instead of guessing a target.
- [Prompt delivery and selection can race with an uncertain Slack outbox acknowledgement] -> Resolve and validate the prompt provider identity through the durable outbox; use the existing uncertain-delivery reconciliation path and stable prompt dispatch reference.
- [A candidate can be disabled or lose access after the prompt is rendered] -> Re-read Connection state and actor access at commit time; return `unavailable` or `stale` and never select a replacement automatically.
- [Persisting source text and attachments increases prompt-row size] -> Store only the normalized, bounded source snapshot and already-accepted attachment descriptors; reject or fall back when the bounded representation cannot be persisted.
- [Adding a Server-authored failed projection may interact with an Agent-authored failure reply] -> Limit the new projection to the failure status/recovery presentation, preserve successful Agent reply ownership, and cover promotion/update ordering with integration tests.
- [Slack Block Kit or action-count limits may be reached for unusually large candidate sets] -> Preserve the readable fallback path and define the operational candidate-count policy before enabling unbounded interactive prompts.

## Migration Plan

1. Add an additive database migration for `SlackRetryOperations` and extend `SlackAmbiguousPrompts` with source, actor, action, expiry, candidate descriptor, selection, recovery-lease, and dispatch/result fields. Keep the existing unique prompt index and backfill existing rows as legacy text-only prompts with no selectable action.
2. Deploy the Server changes that understand both legacy prompt rows and the new action records, including the fixed-key action-recovery reminder. Existing Stop interactions continue through the unchanged behavior; old ambiguous prompts remain readable and are not retrofitted with unverifiable actions.
3. Deploy the terminal event, Server action, prompt-routing, retry-launch, threaded-follow-up, and outbox changes. New failed results and new ambiguous messages can then emit signed controls. Existing adapter versions can carry the already-supported envelope and blocks, but the adapter package should be released with the corresponding contract tests.
4. Monitor rejected/stale action outcomes, operation rows stuck in `dispatch-pending` or `selection-dispatch-pending`, recovery lease expiry, outbox uncertainty, and selection winner conflicts. Add cleanup for expired operation/prompt rows only after the Slack redelivery and delivery-reconciliation retention window.

Rollback is application-first: stop emitting new controls and continue rendering text-only failure and ambiguity fallbacks. The additive columns and operation table can remain while the previous application is deployed. Do not run the down migration while accepted operations or new prompt rows are still within their recovery window; after quiescing the feature, the down migration may remove the new records/columns. A rollback does not cancel an already accepted fresh attempt; its stable session idempotency key prevents duplicate dispatch if the newer build is restored.

## Open Questions

- What action lifetime should apply to Retry and selection? Reusing the existing five-minute Stop lifetime is the simplest default, but the product may want different expiry windows for failure recovery and Bot attribution.
- What candidate-count limit should trigger text-only fallback, given Slack Block Kit limits and the requirement to render one action per eligible Bot?
- Should expired prompt and retry-operation rows be retained for audit longer than the Slack redelivery/reconciliation window, or should they be compacted into a smaller terminal audit record?

The Retry category policy is decided for this change: only the allowlist in Decision 3 can render Retry. A future issue may expand that allowlist with a new reviewed category, but implementation of issue 620 must not infer retryability from arbitrary failure text.
