### Requirement: Cancel applies only to a not-yet-executing Turn

A cancel SHALL take effect only while its target AgentTurn is still queued (not yet executing). A Turn that has already begun executing SHALL NOT be cancelled through the cancel path; the caller MUST use the stop path for an executing Turn.

#### Scenario: Cancel a queued Turn before execution starts
- **WHEN** a cancel is issued for a Turn whose status is queued
- **THEN** that Turn transitions to cancelled and is never dispatched for execution

#### Scenario: Cancel is not applied to an executing Turn
- **WHEN** a cancel is issued for a Turn whose status is executing
- **THEN** the Turn is not cancelled as a cancel and no runtime cancel is dispatched for it

### Requirement: Cancel is deterministic and does not contact the Runtime

Cancelling a queued Turn SHALL be adjudicated entirely on the Server. The cancel SHALL NOT dispatch any request to the Runner or Runtime, SHALL NOT wait for runtime convergence, and SHALL succeed even when no Runner is connected to the Session. Once persisted, the cancelled verdict SHALL be authoritative.

#### Scenario: Cancel succeeds with no Runner connected
- **WHEN** a queued Turn is cancelled while no Runner is bound to its Session
- **THEN** the Turn is recorded as cancelled without contacting any Runner or Runtime

#### Scenario: A cancelled Turn is never dispatched
- **WHEN** a Turn has been cancelled while queued
- **THEN** no later dispatch causes that Turn to enter execution

### Requirement: First-Turn cancel adjudicates the owning AgentJob as cancelled

When the cancelled Turn is the first Turn of an AgentJob launch, the AgentJob SHALL enter a cancelled terminal verdict and the Turn's result SHALL be recorded as cancelled rather than failed. The AgentJob SHALL remain the sole terminal authority for its first Turn. Cancelling any later Turn SHALL NOT rewrite an AgentJob that has already reached a terminal state.

#### Scenario: Cancel the launch Turn
- **WHEN** the first Turn of an AgentJob is cancelled while queued
- **THEN** the AgentJob ends as cancelled and the first Turn's result is recorded as cancelled

#### Scenario: Cancel a later Turn leaves a terminal AgentJob unchanged
- **WHEN** a follow-up Turn is cancelled after the owning AgentJob has already terminated
- **THEN** the AgentJob's terminal verdict is not modified

### Requirement: Cancelled Turns preserve their records and transcript

A cancelled Turn SHALL retain its stable identity, its associated SessionInput records, and every transcript fact already produced. Cancellation SHALL NOT delete the Turn, its inputs, the AgentSession, its transcript, or its lineage.

#### Scenario: Records remain after cancel
- **WHEN** a queued Turn is cancelled
- **THEN** the Turn record, its SessionInput records, the AgentSession, and the transcript remain present and readable

### Requirement: A cancelled Turn returns the Session to idle when no other work remains

After a queued Turn is cancelled and no other non-terminal Turn remains, the AgentSession activity SHALL return to idle so a subsequent accepted input can start its own Turn.

#### Scenario: Session accepts a new Turn after cancel
- **WHEN** the only non-terminal Turn in a Session is cancelled
- **THEN** the AgentSession activity becomes idle and a subsequently accepted input starts a new Turn
