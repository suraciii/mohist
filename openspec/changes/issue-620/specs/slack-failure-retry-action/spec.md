### Requirement: Failed Slack-origin turns SHALL render a Server-owned failure notice
When a Slack-originated execution attempt fails — an initial launch or a threaded follow-up turn — the Server SHALL render an explicit-failure projection for that attempt in the originating Slack conversation. The failure notice MUST replace today's reaction-only closeout as the terminal presentation of an explicit failure: a failed turn MUST produce a readable, Server-authored notice in addition to any terminal reaction. The notice MUST present sanitized failure facts: the failure reason, the failure category when known, and the next step for the user.

#### Scenario: An initial launch fails
- **WHEN** a Slack-origin initial launch reaches a failed terminal state
- **THEN** the Server SHALL render a failure notice for that attempt in the originating conversation or thread
- **AND** the notice SHALL state the failure reason, the failure category when known, and the next step
- **AND** the notice SHALL be delivered through the existing Slack outbox as the terminal explicit-failure projection of that attempt

#### Scenario: A threaded follow-up turn fails
- **WHEN** a Slack-origin follow-up turn in an existing Session reaches a failed terminal state
- **THEN** the Server SHALL render a failure notice for that turn in the same thread
- **AND** the notice SHALL present the same sanitized reason, category, and next-step facts as an initial-launch failure

#### Scenario: Failure facts are sanitized
- **WHEN** the underlying failure carries raw provider errors, credentials, secrets, internal endpoints, or stack traces
- **THEN** the failure notice SHALL render only a readable, sanitized summary of the reason, category, and next step
- **AND** the notice MUST NOT reproduce raw error output, secrets, or internal identifiers verbatim

### Requirement: The failure notice MUST NOT own Agent reply content
The Server-owned failure notice SHALL own only the failure presentation and its recovery control. Agent-authored conversational text SHALL continue to be delivered exclusively through the Agent reply action; the Server MUST NOT author, preview, or replace Agent reply bodies through the failure notice.

#### Scenario: The Agent reply action stays the sole text owner
- **WHEN** a turn fails and the Agent has already delivered or later delivers a reply body through its reply action
- **THEN** the failure notice MUST NOT duplicate, embed, or overwrite that Agent-authored text
- **AND** the reply action's existing promotion and idempotency behavior SHALL remain unchanged

### Requirement: The Retry control SHALL appear only for authoritative retryable failure categories
The Server SHALL attach a Retry action to the failure notice only when the failure carries a category that the authoritative transient-failure category matrix classifies as retryable (for example runner-unavailable, runner-lost, report-timeout, timeout/deadline, transport, and rate-limit categories). Category-less, unknown, and legacy failures, and input, configuration, or permission failures, SHALL render a readable text-only notice with no Retry control. Retryability MUST be decided from the authoritative failure category alone; it MUST NOT be inferred from failure text.

#### Scenario: A retryable transient category attaches the Retry control
- **WHEN** a failed Slack-origin attempt carries an authoritative failure category in the transient retryable matrix (such as runner-unavailable, runner-lost, report-timeout, timeout/deadline, transport, or rate-limit)
- **THEN** the failure notice SHALL include the signed Retry control
- **AND** the Retry control SHALL be the only recovery control the Server attaches to that notice

#### Scenario: Input, configuration, and permission failures stay text-only
- **WHEN** a failed Slack-origin attempt carries an input, configuration, or permission failure category
- **THEN** the failure notice SHALL present the readable reason, category, and next step
- **AND** the notice MUST NOT include any Retry control

#### Scenario: Category-less, unknown, and legacy failures stay text-only
- **WHEN** a failed Slack-origin attempt carries no failure category, an unknown category, or a legacy failure fact set
- **THEN** the failure notice SHALL remain readable with its available facts and next-step guidance
- **AND** the notice MUST NOT include any Retry control

#### Scenario: Retryability is never inferred from text
- **WHEN** a failure message text mentions transient-sounding words (such as "timeout" or "unavailable") but the authoritative category is not in the retryable matrix
- **THEN** the Server MUST NOT attach a Retry control based on that text

### Requirement: The Retry action SHALL be signed, expiring, and bound to actor and context
The Retry control SHALL be a Server-signed action with its own action id, distinct from the Stop action id. Its value SHALL carry a canonical payload binding the failed attempt's identity, the acting Slack member, the Connection, workspace, conversation, message, and thread context, a unique nonce, and a bounded expiry. The signature SHALL be an HMAC computed with a key bound to the verified Connection credential, and verification SHALL compare signatures in constant time. A Retry action whose required binding facts or signing key cannot be established MUST NOT be created.

#### Scenario: Creation embeds the binding facts
- **WHEN** the Server attaches a Retry control to a failure notice
- **THEN** the action value SHALL bind the failed attempt's session, input, and turn identity, the actor's Slack user id, the Connection id, workspace team id, conversation id, message ts, and thread ts when present
- **AND** the value SHALL carry a unique nonce and an expiry within a bounded lifetime

#### Scenario: No signing key means no Retry control
- **WHEN** the Connection's verified signing credential cannot be loaded
- **THEN** the Server MUST NOT attach a Retry control to the failure notice
- **AND** the failure notice SHALL still render its readable text

### Requirement: Retry clicks SHALL be verified and re-authorized at the interaction boundary
Retry clicks SHALL be handled at the existing Slack interaction boundary under the validated adapter lease, with no new adapter-facing endpoint. On every click the Server SHALL verify the action version and discriminator, the signature in constant time, the expiry, and the freshness, and SHALL re-check that the payload's Connection, workspace, conversation, and message binding still matches the live Connection and the click's context. The Server SHALL then re-evaluate the clicking actor through the current Connection access policy: a payload-bound actor alone is not authorization. Invalid, tampered, expired, stale, replayed, or unauthorized clicks SHALL produce an explicit user-visible outcome with no execution side effect.

#### Scenario: A tampered or malformed action value is rejected
- **WHEN** a Retry click arrives with an altered signature, an unsupported version, or an unparseable value
- **THEN** the Server SHALL reject it with an explicit user-visible outcome
- **AND** no execution side effect SHALL occur

#### Scenario: An expired action is rejected
- **WHEN** a Retry click arrives after the action's expiry
- **THEN** the Server SHALL report an explicit expired outcome to the actor
- **AND** no execution side effect SHALL occur

#### Scenario: A context or actor mismatch is rejected
- **WHEN** a Retry click's workspace, conversation, or message context no longer matches the signed payload, or the click's actor differs from the payload-bound actor
- **THEN** the Server SHALL report an explicit stale or unauthorized outcome
- **AND** no execution side effect SHALL occur

#### Scenario: A bound actor who lost access is unauthorized
- **WHEN** the payload-bound actor clicks a still-valid Retry action but the current Connection access policy no longer authorizes that actor to invoke the Connection
- **THEN** the Server SHALL report an explicit unauthorized outcome
- **AND** no execution side effect SHALL occur

### Requirement: Every Retry outcome SHALL produce an explicit, durable presentation update
Every Retry click outcome SHALL be delivered through the existing Slack outbox and SHALL update the failed notice's presentation. An accepted retry SHALL acknowledge the new attempt and project its working state, including a newly signed Stop control where the new attempt is stoppable. Rejected, stale, unavailable, already-applied, and replayed results SHALL remove or replace the obsolete Retry control on the failure notice.

#### Scenario: An accepted retry projects the new attempt
- **WHEN** a Retry click is accepted and starts a fresh attempt
- **THEN** the failed notice's presentation SHALL be updated to acknowledge the retry and project the new attempt's working state
- **AND** the projection SHALL include a signed Stop control when the new attempt is in a stoppable state

#### Scenario: A rejected or replayed click retires the Retry control
- **WHEN** a Retry click is rejected, stale, unavailable, already applied, or replayed
- **THEN** the obsolete Retry control SHALL be removed from or replaced on the failure notice with the explicit outcome
- **AND** the update SHALL be delivered through the existing outbox durability and retry semantics

### Requirement: The adapter SHALL pass the Retry control through unchanged
The Slack adapters SHALL treat the Retry action as pure pass-through. The TypeScript adapter and the Go transport port SHALL forward the Retry action id and signed value unchanged in the interaction envelope under the existing lease identity, SHALL deliver Server-provided blocks unchanged, and SHALL acknowledge Slack promptly exactly as they do for Stop. The adapters MUST NOT gain new action grammar, new endpoints, or authorization logic for Retry.

#### Scenario: The adapter forwards a Retry click unchanged
- **WHEN** a Slack block action carrying the Retry action id and signed value reaches either adapter
- **THEN** the adapter SHALL forward the action id and value unchanged to the existing interaction boundary with its lease identity
- **AND** the adapter SHALL acknowledge Slack promptly without performing Retry-specific processing

#### Scenario: The adapter delivers Server blocks unchanged
- **WHEN** the Server enqueues a failure notice, its Retry blocks, or any Retry outcome presentation
- **THEN** both adapters SHALL deliver the Server-provided blocks unchanged, without rewriting, filtering, or interpreting them

### Requirement: Legacy terminal events SHALL remain renderable without a Retry control
The terminal delivery contracts for initial-launch and follow-up failure events SHALL be extended additively so failed events carry the session, input, and turn identity and the failure category needed to authorize a retry deterministically. Terminal events without those identity or category facts (legacy events) SHALL remain renderable as failure notices with their available facts, and they MUST NOT expose a Retry control.

#### Scenario: A legacy event renders without Retry
- **WHEN** a terminal failure delivery event arrives without the retry-authorizing session, input, or turn identity or without a failure category
- **THEN** the Server SHALL still render a readable failure notice from the available facts
- **AND** the notice MUST NOT expose any Retry control

#### Scenario: New events carry deterministic retry facts
- **WHEN** a failed initial-launch or follow-up terminal delivery event is emitted for a Slack-origin attempt
- **THEN** the event SHALL carry the session, input, and turn identity and the authoritative failure category
- **AND** those facts SHALL be sufficient for the Server to decide retryability and authorize a Retry action without consulting failure text
