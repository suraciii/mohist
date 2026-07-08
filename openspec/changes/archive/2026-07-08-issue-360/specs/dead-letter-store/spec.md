### Requirement: A DeadLetters table isolates poison messages from the live event tables

A `DeadLetters` table SHALL exist, physically and logically separate from `WorkflowRunEvents`, `IssueEvents`, and `EpicEvents`, so that a message which has exhausted its retries is removed from the live delivery path and parked where it cannot be re-offered by the undelivered query. The table is an isolation zone for diagnosis and later manual replay, not a retry queue.

#### Scenario: A dead-lettered message leaves the live delivery path

- **WHEN** a poison message is written to the `DeadLetters` table
- **THEN** that record SHALL reside in `DeadLetters` and not in `WorkflowRunEvents`, `IssueEvents`, or `EpicEvents`
- **AND** the dead-letter record SHALL NOT be returned by the unified undelivered query, which scans only the three live event tables

### Requirement: Each dead-letter record captures the full diagnostic snapshot of the failed delivery

Each `DeadLetters` row SHALL capture, at minimum: the original event envelope snapshot (sufficient to reconstruct the CloudEvent — `Source`, `EventId`, `Type`, `Time`, `Subject`, `Data`, `DataContentType`, `SpecVersion`, and extensions), the identifier of the failing handler, the terminal error (message and/or stack trace), and the attempt count at the point of dead-lettering. These fields SHALL be enough to diagnose the failure and, in a later issue, to manually replay the event. The snapshot SHALL be immutable once written — it records the state at failure time, not a live reference that can drift.

#### Scenario: A written dead-letter record contains the envelope, handler, error, and attempt count

- **WHEN** a dead-letter record is written for a poison message
- **THEN** the persisted record SHALL contain the original event envelope snapshot
- **AND** SHALL contain the identifier of the failing handler
- **AND** SHALL contain the terminal error
- **AND** SHALL contain the attempt count

#### Scenario: The captured envelope is sufficient to reconstruct the event

- **WHEN** the envelope snapshot stored on a dead-letter record is read back
- **THEN** it SHALL carry enough of the CloudEvent attributes (`Source`, `EventId`, `Type`, `Time`, `Subject`, `Data`, `DataContentType`, `SpecVersion`, extensions) to reconstruct the original event without consulting the live event tables

### Requirement: The dead-letter store port supports writing and querying records

The storage layer SHALL expose a port with two operations over `DeadLetters`: write a dead-letter record, and query existing dead-letter records. Writing SHALL append a new record (the table is append-only isolation, not an in-place retry queue). Querying SHALL return records that have been written, so operators and later tooling can inspect the poison backlog. The port SHALL NOT expose automated replay in this change — replay is explicitly out of scope and reserved for a later issue.

#### Scenario: A record written can later be queried

- **WHEN** a dead-letter record is written through the port
- **AND** the query operation is subsequently invoked
- **THEN** the written record SHALL appear in the query result

#### Scenario: The dead-letter store is append-only

- **WHEN** a dead-letter record has been written
- **THEN** no operation exposed by this change SHALL mutate or remove that record in place
- **AND** writing a second record for the same logical failure SHALL append a new row rather than overwrite the first

#### Scenario: No automated replay is exposed by this change

- **WHEN** the dead-letter store port's surface is inspected
- **THEN** it SHALL expose only write and query operations
- **AND** it SHALL NOT expose any replay, requeue, or delete operation
