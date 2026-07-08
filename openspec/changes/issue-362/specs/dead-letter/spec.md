### Requirement: Poison messages dead-lettered on retry exhaustion

When a handler's transient-failure retries are exhausted on a poison message, the event SHALL be written to the dead-letter table with the failing handler, the failure reason, and the attempt count. The dispatcher SHALL then set `DispatchedAt` on the original event row so that the dispatcher stops retrying it on subsequent ticks.

#### Scenario: Exhausted handler moves the event to the dead-letter table

- **WHEN** a handler's retries are exhausted on an event
- **THEN** the event SHALL be written to the dead-letter table
- **AND** the dead-letter row SHALL record the failing handler, the error, and the attempt count
- **AND** the original event row's `DispatchedAt` SHALL be set
- **AND** the dispatcher SHALL NOT retry that row on subsequent ticks

### Requirement: Per-handler isolation

Exhaustion on one handler SHALL NOT block delivery of the same event to other matching handlers. Each handler's retry and dead-letter outcome SHALL be tracked independently, so the failure of one reaction does not suppress the co-occurring reactions for the same event.

#### Scenario: A sibling handler still receives the event

- **WHEN** an event matches two handlers and one handler exhausts its retries
- **THEN** the other matching handler SHALL still receive the event independently
- **AND** its outcome (success or its own exhaustion) SHALL NOT depend on the failing handler

### Requirement: Dead-letter rows queryable

Dead-letter rows SHALL be queryable so operators can inspect poison messages. The query SHALL support filtering by failing handler.

#### Scenario: Query dead-lettered events

- **WHEN** an operator queries the dead-letter store
- **THEN** every dead-lettered event SHALL be returned with its failing handler, error, and attempt count
- **AND** the query SHALL support filtering by failing handler

### Requirement: Dead-letter rows manually re-deliverable

A dead-lettered event SHALL be manually re-deliverable on operator action, so a poison message can be re-dispatched to its handlers after the underlying cause is resolved.

#### Scenario: Operator requests re-delivery

- **WHEN** an operator requests re-delivery of a dead-lettered event
- **THEN** the event SHALL be re-dispatched to its matching handlers
