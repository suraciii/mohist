### Requirement: Retry action appears only on retryable failure notices

The terminal failure presentation of a failed Turn whose recorded failure category is retryable MUST include a Retry action button carrying a Server-signed action payload with a five-minute expiry. A failed Turn whose recorded failure category is not retryable MUST keep its current text-and-reaction-only terminal presentation with no Retry action. Completion, cancellation, and other non-failure terminal presentations MUST NOT render a Retry action.

#### Scenario: Retryable failure renders the Retry action

- **WHEN** the terminal failure notice for a failed Turn is projected to Slack and the Turn's recorded failure category is in the retryable allowlist
- **THEN** the notice MUST render a Retry action button
- **AND** the button's action value MUST carry an expiry no more than five minutes after the action was created

#### Scenario: Non-retryable failure keeps the current presentation

- **WHEN** the terminal failure notice for a failed Turn is projected to Slack and the Turn's recorded failure category is not retryable or is absent
- **THEN** the notice MUST NOT render any Retry action
- **AND** the existing text/reaction presentation MUST be unchanged

#### Scenario: Non-failure terminal status has no Retry action

- **WHEN** a Turn terminates as completed, cancelled, or otherwise not failed
- **THEN** the terminal presentation MUST NOT render a Retry action

### Requirement: Server-signed, operator-bound action payload

The Retry action value MUST be a payload signed by the Server with the Connection's signing material that binds the action version, Connection id, Session id, Turn id, the Slack conversation and message identity it was rendered for, the operator Slack user id it is bound to, a unique nonce, and the expiry timestamp. Verification MUST compare signatures in constant time and MUST reject payloads with an invalid version, action name, missing nonce, or missing signature.

#### Scenario: Tampered payload is rejected

- **WHEN** a Retry interaction arrives whose action value has been modified after signing, or whose signature, nonce, version, or action name is absent or invalid
- **THEN** the interaction MUST be rejected as an invalid action
- **AND** no execution resources MUST be created

#### Scenario: Signing material unavailable suppresses the action

- **WHEN** the Server cannot load signing material for the Connection at presentation time
- **THEN** no Retry action MUST be rendered
- **AND** the failure notice keeps a presentation without a Retry button

### Requirement: Acceptance revalidates signature, freshness, context, actor, permissions, and target state

On a Retry click the system MUST revalidate, in one acceptance path: the action signature; the five-minute freshness window; the context match (Connection id, workspace team id, and conversation id of the interaction against the payload); the operator binding (the clicking Slack member MUST be the operator the action was issued to); that operator's current permission (Connection Owner or the session initiator) under the Connection's current access policy; and the target Turn's current authoritative failure facts. Every failed check MUST produce an explicit rejection outcome, and a rejected click MUST create no execution resources.

#### Scenario: Expired action

- **WHEN** a Retry click arrives after the action's expiry timestamp
- **THEN** the click MUST be rejected with an expired outcome and no execution resources MUST be created

#### Scenario: Stale context

- **WHEN** a Retry click's Connection id, workspace team id, or conversation id no longer matches the signed payload
- **THEN** the click MUST be rejected as stale and no execution resources MUST be created

#### Scenario: Different Slack member clicks

- **WHEN** a Slack member other than the operator the Retry action was bound to clicks the button
- **THEN** the click MUST be rejected as unauthorized and no execution resources MUST be created

#### Scenario: Operator no longer permitted

- **WHEN** the bound operator is no longer the Connection Owner or the session initiator, or the Connection's current access policy denies the operator
- **THEN** the click MUST be rejected as unauthorized and no execution resources MUST be created

#### Scenario: Target no longer retryable

- **WHEN** the target Turn's current authoritative failure facts are no longer those of a failed Turn with a retryable category
- **THEN** the click MUST be rejected with a no-longer-retryable outcome and no execution resources MUST be created

#### Scenario: Disabled Connection

- **WHEN** a Retry click arrives for a Connection whose desired state is disabled
- **THEN** the interaction MUST be rejected and no execution resources MUST be created

### Requirement: Retry coexists with Stop on the same interaction path

The Retry action MUST be delivered through the existing Slack interaction route that serves the Stop action, reusing unchanged the adapter lease validation and the outbox user-action reply delivery. Adding the Retry action id MUST NOT change the behavior, signature, lifetime, or rejection outcomes of the existing Stop action.

#### Scenario: Retry click is routed through the shared interaction route

- **WHEN** a `block_actions` interaction with the Retry action id arrives at the interaction route
- **THEN** it MUST pass the same adapter lease validation and Connection resolution as a Stop click before acceptance
- **AND** the acceptance result MUST be delivered back to Slack through the outbox user-action reply path

#### Scenario: Stop behavior is unchanged

- **WHEN** a Stop click arrives before or after Retry actions exist on the same route
- **THEN** the Stop action's signature, five-minute lifetime, revalidation, and outcome states MUST behave exactly as before the Retry action was introduced

### Requirement: The button is a shortcut to the shared retry application service

The Retry click MUST dispatch to the same application service a CLI or Web retry uses. The interaction route MUST NOT implement a second, parallel retry command grammar; the button is only an authorized shortcut onto the single retry operation surface.

#### Scenario: Button and CLI/Web surface converge

- **WHEN** a Retry click is accepted and a CLI or Web retry for the same failed Turn is accepted
- **THEN** both MUST flow through the same retry application service and produce operation records of the same shape with the same invariants
