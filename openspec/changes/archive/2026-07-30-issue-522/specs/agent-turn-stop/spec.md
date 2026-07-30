### Requirement: Stop requests the Runtime to halt a specific executing Turn

A stop SHALL request the bound Runtime to interrupt the execution of its target AgentTurn. The stop SHALL be presented to the caller as a request whose effect depends on Runtime convergence, not as an immediately guaranteed halt. The stop path SHALL apply only to a Turn that is executing; a queued Turn is not stoppable through the stop path.

#### Scenario: Stop an executing Turn
- **WHEN** a stop is issued for a Turn whose status is executing
- **THEN** a stop request is sent to the bound Runtime for that Turn and the result reflects what the Runtime actually observed

#### Scenario: Stop is not applied to a queued Turn
- **WHEN** a stop is issued for a Turn whose status is queued
- **THEN** no runtime stop request is dispatched and the caller is directed to the cancel path

#### Scenario: A stale stop cannot abort a later Turn
- **WHEN** the target Turn becomes terminal while its stop request is being dispatched
- **THEN** the Session does not admit a later Turn until that stop request settles, and the old request cannot abort later work

### Requirement: Stop confirmation is honest

The stop result SHALL distinguish a confirmed stop from an unconfirmed stop by surfacing the state the Runtime reported. The API SHALL NOT report a successful stop when the stop could not be confirmed.

#### Scenario: Runtime confirms the stop
- **WHEN** the bound Runtime confirms that execution has halted
- **THEN** the stop result reports the Turn as stopped (confirmed)

#### Scenario: Runtime cannot confirm the stop
- **WHEN** the bound Runtime issues the stop request but cannot confirm that execution has actually halted
- **THEN** the stop result surfaces the unconfirmed stop and does not claim the Turn was safely stopped

### Requirement: An unconfirmed stop leaves the Turn and Session activity Unknown

When a stop result cannot be confirmed, the target Turn and the AgentSession activity SHALL remain Unknown. The system SHALL NOT automatically create a new Turn, replay any already-accepted SessionInput, synthesise an idle, completed, or failed verdict, or retry the stop without the caller's involvement. The Unknown state SHALL reconcile only on authoritative Runtime evidence.

#### Scenario: Unconfirmed stop preserves Unknown
- **WHEN** a stop request is issued and its result cannot be confirmed
- **THEN** the target Turn stays Unknown and the AgentSession activity stays Unknown

#### Scenario: No replay or new Turn after an unconfirmed stop
- **WHEN** a stop ends unconfirmed
- **THEN** no new Turn is created, no already-accepted SessionInput is replayed, and no idle, completed, or failed verdict is synthesised for the target Turn

### Requirement: A later Turn's stop does not rewrite a terminal AgentJob

For the first Turn, the stop outcome continues to be adjudicated by the owning AgentJob. For any later Turn, a stop SHALL NOT rewrite an AgentJob that has already reached a terminal state. Turn result and AgentJob status SHALL remain independent facts.

#### Scenario: Stop a follow-up Turn leaves a terminal AgentJob unchanged
- **WHEN** a stop is issued for a later Turn after the owning AgentJob has already terminated
- **THEN** the AgentJob's terminal verdict is not modified

### Requirement: Stopped Turns preserve their records and transcript

A stopped Turn SHALL retain its stable identity, its associated SessionInput records, every transcript fact already produced, and any partial Runtime output captured before the stop. A stop SHALL NOT delete the Turn, its inputs, the AgentSession, its transcript, or its lineage.

#### Scenario: Records remain after stop
- **WHEN** an executing Turn is stopped
- **THEN** the Turn record, its SessionInput records, the AgentSession, the transcript, and any partial output remain present and readable
