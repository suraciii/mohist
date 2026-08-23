### Requirement: A root-message retry launches a new Session

When the retried failed Turn was the initial root-message launch of its Session, the retry MUST create a new execution attempt as a new Session built from the failed Turn's original durable Slack provenance (Connection, workspace team, conversation, message identity, and initiating member) and the recorded execution facts of the original launch. The new Session MUST receive its own newly minted identities.

#### Scenario: Root retry creates a new Session

- **WHEN** a Retry click is accepted for a failed Turn that was the initial root-message launch of its Session
- **THEN** the system MUST launch a new Session carrying the original durable Slack provenance
- **AND** the new Session MUST use the execution identity pre-allocated for the Retry operation
- **AND** the new Session's launch MUST follow the same launch path and invariants as the original Slack-launched Session

#### Scenario: New Session is distinct from the failed one

- **WHEN** the root retry attempt is created
- **THEN** it MUST have a Session identity distinct from the original failed Session
- **AND** the two Sessions MUST be independently observable in audit and provenance

### Requirement: A thread retry creates an explicitly targeted follow-up in the original Session

When the retried failed Turn was a thread follow-up, the retry MUST create a new follow-up operation explicitly targeted at that thread inside the original Session, bound to the original durable Slack provenance of the failed Turn. The dispatch MUST NOT schedule, promote, or dispatch any unrelated queued turn of that Session.

#### Scenario: Thread retry dispatches only the targeted follow-up

- **WHEN** a Retry click is accepted for a failed thread follow-up Turn in a Session that also has unrelated queued turns
- **THEN** the system MUST create one explicitly targeted new follow-up operation for the retried thread in the original Session
- **AND** the unrelated queued turns MUST remain queued and MUST NOT be dispatched as part of the retry

#### Scenario: Thread binding is preserved

- **WHEN** the targeted follow-up dispatch is built
- **THEN** its provenance MUST bind to the same thread root and conversation as the original failed Turn's durable Slack provenance

### Requirement: The failed Turn stays immutable

A retry MUST NOT modify, reopen, relabel, or re-dispatch the failed Turn. The failed Turn's recorded status, failure reason, and failure category remain authoritative facts of the failed attempt, and the new attempt MUST carry fresh execution identities rather than reusing the failed Turn's.

#### Scenario: Failure facts survive the retry

- **WHEN** a retry attempt is created and dispatched
- **THEN** the original failed Turn MUST still record its original status, failure reason, and failure category unchanged
- **AND** the new attempt MUST be recorded under its own pre-allocated turn, input, and dispatch identities

### Requirement: Slack shows that the attempt was accepted

Once a Retry click is accepted and the new attempt operation is committed, the system MUST surface acceptance feedback in Slack through the outbox user-action reply path, using the same identity-stable dispatch reference behavior that makes replies idempotent.

#### Scenario: Accepted click produces visible feedback

- **WHEN** a Retry click is accepted and its operation committed
- **THEN** Slack MUST receive an update or reply stating that the new attempt was accepted
- **AND** redelivery or replay of the same click MUST NOT duplicate that feedback

#### Scenario: Rejected click produces explicit rejection feedback

- **WHEN** a Retry click is rejected for expiry, staleness, tampering, unauthorized actor, or a no-longer-retryable target
- **THEN** Slack MUST receive the explicit rejection outcome text for that reason
- **AND** no attempt-accepted feedback MUST be posted
