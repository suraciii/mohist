### Requirement: Dispatch snapshot is stored separately from WorkflowRun State
The dispatch snapshot for a task attempt SHALL NOT be persisted as part of the WorkflowRun State. A TaskRun SHALL NOT embed a `WorkDispatch`. The snapshot SHALL reside in a store separated from the run State, and the redelivery path SHALL load it on demand from that separated store rather than from the State column.

#### Scenario: Dispatched task State excludes the snapshot
- **WHEN** a task attempt is dispatched and the WorkflowRun State is persisted
- **THEN** the persisted State SHALL NOT contain the `WorkDispatch` payload for that attempt
- **AND** the snapshot SHALL be retrievable from the separated store, keyed by the attempt

#### Scenario: Redelivery loads the snapshot from the separated store
- **WHEN** a redelivery poll arrives for a Running task attempt that has a stored snapshot
- **THEN** the server SHALL load the snapshot from the separated store, not by deserializing the run State

### Requirement: Snapshot is created once and never overwritten
The dispatch snapshot SHALL be generated on first dispatch and SHALL NOT change afterward (first-write-wins). A subsequent store request for the same Running attempt SHALL return the already-stored snapshot without overwriting it.

#### Scenario: Idempotent store returns the first snapshot
- **WHEN** the dispatch store is called more than once for the same Running attempt
- **THEN** every call after the first SHALL return the snapshot stored by the first call, unchanged

### Requirement: Redelivery replays the verbatim first snapshot
While a task attempt is Running (dispatched, terminal not yet reported), poll redelivery SHALL return the stored snapshot verbatim. Changes to effective variables, prompt bodies, or other dispatch inputs after the first dispatch SHALL NOT alter the redelivered snapshot. When no snapshot is stored yet, the server SHALL render it via the dispatch translator and store it on first access.

#### Scenario: Variable change after dispatch does not affect redelivery
- **WHEN** a task attempt is dispatched with an effective variable value A, the variable is then changed to B, and a redelivery poll arrives while the attempt is still Running
- **THEN** the redelivered snapshot SHALL carry the value A bound to the first dispatch moment

#### Scenario: First redelivery renders and stores
- **WHEN** a redelivery poll arrives for a Running attempt that has no stored snapshot yet
- **THEN** the server SHALL render the dispatch via the translator, store the resulting snapshot, and return it
- **AND** the next redelivery for the same attempt SHALL return the stored snapshot without re-rendering

### Requirement: Terminal or superseded attempts drop the snapshot immediately
When a task attempt reaches a terminal state (Completed or Failed) or is superseded by a later attempt for the same task definition (retry), its dispatch snapshot SHALL be invalidated immediately and SHALL NOT be retained.

#### Scenario: Completed attempt drops the snapshot
- **WHEN** a Running task attempt reports Completed
- **THEN** its dispatch snapshot SHALL no longer be retained in the separated store

#### Scenario: Failed attempt drops the snapshot
- **WHEN** a Running task attempt reports Failed
- **THEN** its dispatch snapshot SHALL no longer be retained

#### Scenario: Retry supersedes the failed attempt's snapshot
- **WHEN** a failed task attempt is retried and a new attempt for the same task definition is created
- **THEN** the superseded failed attempt's snapshot SHALL no longer be retained
- **AND** only the new attempt's snapshot, once dispatched, SHALL be available

#### Scenario: Stopped run drops the snapshot
- **WHEN** a run is stopped while a task attempt is Running
- **THEN** the attempt's snapshot SHALL no longer be retained

### Requirement: Checks dispatch never persists a snapshot
Checks dispatch SHALL NOT persist a snapshot. Redelivery for checks SHALL always reconstruct the dispatch via the dispatch translator.

#### Scenario: Checks redelivery reconstructs without storing
- **WHEN** checks are dispatched and subsequently redelivered
- **THEN** no snapshot SHALL be stored for the checks work
- **AND** each redelivery SHALL reconstruct the dispatch via the translator

### Requirement: Upgrade preserves in-flight attempt redelivery
A run with a Running task attempt at upgrade time SHALL retain snapshot availability for redelivery until that attempt reports terminal. An upgraded server SHALL NOT lose the ability to redeliver an in-flight attempt's snapshot.

#### Scenario: In-flight snapshot survives upgrade
- **WHEN** a run has a Running attempt whose snapshot is stored at the moment the server is upgraded to the separated-store format
- **THEN** after upgrade the redelivery path SHALL still return that attempt's snapshot verbatim until the attempt reports terminal
