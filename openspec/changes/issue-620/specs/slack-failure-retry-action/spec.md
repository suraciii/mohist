### Requirement: Failed Slack results expose a signed Retry action
A retryable failed Slack result MUST include a Server-generated Block Kit Retry action in the terminal presentation. The action payload MUST identify the original Slack Connection, session or work target, failed input or turn, workspace, conversation, message and thread context, actor, a single-use nonce, and an expiry. The payload MUST be signed with the verified Slack Connection credential, and the rendered payload MUST NOT contain a Slack credential. For this capability, retryable means terminal status `failed` plus one of the exact categories `runner-unavailable`, `runner-lost`, `report-timeout`, `timeout`, `deadline-exceeded`, `probe_timeout`, `opencode-transport-failed`, `unavailable-runtime`, `rate_limited`, or `retry-safe`, compared case-insensitively after trimming. The Server MUST NOT infer retryability from failure text.

#### Scenario: A retryable failure is rendered with Retry
- **WHEN** a Slack execution reaches a retryable failed terminal state
- **THEN** the terminal delivery contains readable failure text and exactly one signed Retry control bound to that failed execution and its original Slack context

#### Scenario: A non-retryable terminal result is rendered
- **WHEN** a Slack execution reaches a completed, cancelled, or otherwise non-retryable terminal state
- **THEN** the terminal delivery does not expose a Retry control for that execution

#### Scenario: Retryability uses the authoritative category matrix
- **WHEN** a failed initial launch or follow-up has an allowlisted category
- **THEN** the terminal delivery exposes exactly one Retry control
- **AND WHEN** the failed result has no category, category `unknown`, a legacy event without the required facts, an unrecognized category, `invalid-input`, `permission-required`, `configuration`, `context_exhausted`, `runtime-session-missing`, or generic `turn-failed`
- **THEN** the terminal delivery is readable but text-only and exposes no Retry control

### Requirement: Retry interactions are verified and authorized at the Server boundary
The Server MUST accept a Retry interaction only when the normalized event is a supported Block Kit action, the signature is valid under constant-time comparison, the action is unexpired, and the payload matches the receiving Connection, Slack workspace, conversation, message or thread context, and bound actor. The Server MUST re-read the authoritative session and failed-turn state before dispatching. Invalid, tampered, expired, wrong-Connection, wrong-context, or unauthorized actions MUST produce an explicit user-visible outcome and MUST NOT invoke runtime control or launch work.

#### Scenario: The bound actor retries a still-failed execution
- **WHEN** the original actor selects a valid, unexpired Retry action while the referenced execution is still retryable and failed
- **THEN** the Server accepts the action for dispatch and reports an accepted outcome without requiring the actor to reconstruct the Slack request

#### Scenario: A tampered or expired Retry action is selected
- **WHEN** the action value has been changed or its expiry has passed
- **THEN** the Server reports an invalid or expired outcome, emits no execution side effect, and presents the failure of the action in Slack

#### Scenario: An unauthorized actor selects Retry
- **WHEN** a Slack member other than the actor bound into the signed payload selects the action
- **THEN** the Server reports an unauthorized outcome, emits no execution side effect, and presents the rejection in Slack

#### Scenario: The action is delivered to the wrong Slack context
- **WHEN** a valid signed action is received for a different Connection, workspace, conversation, message, or thread than the payload identifies
- **THEN** the Server reports a stale outcome, emits no execution side effect, and presents the rejection in Slack

### Requirement: An accepted Retry starts one fresh attempt from the original Slack context
An accepted Retry MUST create or dispatch a fresh execution attempt with a new attempt identity while preserving the original Slack request, actor, Connection, conversation, thread, and reply-routing context. The new attempt MUST be handled through the existing durable session, inbox, and outbox boundaries. The failed attempt MUST NOT be mutated into a successful attempt, and a Retry MUST NOT dispatch to another Connection or conversation. A root retry uses a new Session plus new initial input and turn under a retry-specific launch idempotency key; a threaded retry uses a new follow-up input and turn in the existing Session under that retry-specific key.

#### Scenario: A failed root request is retried
- **WHEN** a valid Retry action is accepted for a failed root Slack request
- **THEN** a new Session, initial input, and initial turn are queued through the normal launch coordinator with a retry-specific idempotency key
- **AND** the new attempt retains the original Connection, actor, workspace, conversation, source message, and root reply target, while no second attempt is created for any other Bot

#### Scenario: A failed threaded request is retried
- **WHEN** a valid Retry action is accepted for a failed threaded Slack request
- **THEN** a new follow-up input and turn are accepted in the original Session and queued through the existing follow-up dispatcher with the retry-specific idempotency key
- **AND** the new attempt remains in the original thread with the original provenance and the failed result remains an immutable historical outcome

### Requirement: Retry dispatch is idempotent across replay and redelivery
The Server MUST persist a stable Retry operation identity before dispatching the fresh attempt. Repeated delivery of the same action value, Slack redelivery, adapter failover, or a concurrent request MUST collapse to the same operation result. The Server MUST NOT start more than one fresh attempt for one signed Retry action. A committed `dispatch-pending` operation MUST be resumable by a fixed-key durable Server recovery reminder even after the interaction request has been acknowledged or the original process has restarted. The interaction route MUST re-enter a pending operation on replay instead of returning only a duplicate-receipt result.

#### Scenario: The same Retry action is selected twice
- **WHEN** the first selection has been accepted and the same signed action is selected again
- **THEN** the second selection reports already applied or replayed, starts no new attempt, and does not enqueue a duplicate execution or terminal status delivery

#### Scenario: Concurrent adapters submit the same Retry action
- **WHEN** two valid submissions for one Retry action arrive concurrently
- **THEN** exactly one submission owns the Retry operation, all submissions converge on its recorded outcome, and only one fresh attempt is dispatched

#### Scenario: A Retry operation was recorded before an adapter failure
- **WHEN** the adapter fails over or redelivers after the Server recorded acceptance but before the original response was observed
- **THEN** the retry operation is recovered by its stable identity and the fresh attempt is not duplicated

#### Scenario: The Server process dies after the pending-operation commit
- **WHEN** the Server commits `dispatch-pending` and the process dies before the launch or follow-up dispatcher is called
- **THEN** the fixed-key recovery reminder claims the pending operation after restart and invokes the same operation resume path with the same retry dispatch key
- **AND** a concurrent interaction replay can either perform that resume or observe its recovery lease, but neither path creates a second attempt

### Requirement: Retry validates terminal state before taking effect
The Server MUST distinguish an available failed target from a target that has already been retried, has changed terminal state, is still running, has been replaced, or cannot be resolved. These terminal-state checks MUST happen before any fresh-attempt dispatch and MUST return explicit states for accepted, already applied, stale, and unavailable cases.

#### Scenario: The failed target is no longer the current retryable target
- **WHEN** a Retry action references a failed turn that has been superseded or otherwise no longer matches the authoritative session state
- **THEN** the Server reports stale, starts no fresh attempt, and updates Slack with an explanatory result

#### Scenario: The referenced work is unavailable for retry
- **WHEN** the session, Connection, or durable source context needed for retry cannot be resolved, or the target is not in a retryable failed state
- **THEN** the Server reports unavailable, starts no fresh attempt, and updates Slack with an explanatory result

### Requirement: Retry outcomes update the Slack presentation through durable delivery
Every Retry result MUST be represented by a Server-owned outbox delivery addressed to the original Slack message or its established fallback target. An accepted result MUST acknowledge the retry and project the new attempt's working state, including the existing signed Stop behavior when that control is applicable. Rejected, stale, unavailable, already-applied, and replayed results MUST be explicit and MUST remove or replace the obsolete Retry control so a stale action is not presented as available work.

#### Scenario: Retry is accepted and work resumes
- **WHEN** the Server accepts a Retry action and queues the fresh attempt
- **THEN** Slack receives an idempotent update acknowledging acceptance and showing the new working state with applicable controls

#### Scenario: Retry is rejected after the failure becomes stale
- **WHEN** the Server rejects a Retry because the target changed or is unavailable
- **THEN** Slack receives a readable update describing that the action is no longer available and no enabled Retry control remains for that target

### Requirement: The adapter forwards Retry controls without interpreting them
The Slack adapter MUST normalize Retry Block Kit interactions into the existing interaction envelope with the action identifier and signed value intact, MUST omit raw Slack payloads and credentials, MUST acknowledge Slack before waiting for Server processing, and MUST forward Server-provided text and blocks through the delivery contract. The adapter MUST preserve the existing signed Stop interaction behavior.

#### Scenario: Slack delivers a Retry click
- **WHEN** the Socket Mode adapter receives a Retry `block_actions` event
- **THEN** it acknowledges the Slack interaction promptly, forwards the normalized actor, context, action identifier, and signed value to the Server, and does not select a target or authorize the action locally

#### Scenario: The Server returns Retry result blocks
- **WHEN** the adapter claims a Retry result delivery containing text and Block Kit blocks
- **THEN** it sends those exact Server-provided presentation fields to Slack and acknowledges the outbox delivery using the existing idempotent delivery protocol
