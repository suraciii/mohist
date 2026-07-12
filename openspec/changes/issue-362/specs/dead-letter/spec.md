### Requirement: Poison messages dead-lettered on retry exhaustion

When a handler's transient-failure retries are exhausted on a poison message, the event SHALL be written to the dead-letter table with the failing handler, the failure reason, and the attempt count. The dead-letter write and the original event's `DispatchedAt` update SHALL commit atomically. A natural key SHALL keep one row per source event and failing handler.

#### Scenario: Exhausted handler moves the event to the dead-letter table

- **WHEN** a handler's retries are exhausted on an event
- **THEN** the event SHALL be written to the dead-letter table
- **AND** the dead-letter row SHALL record the failing handler, the error, and the attempt count
- **AND** the original event row's `DispatchedAt` SHALL be set
- **AND** both persistence changes SHALL commit in one transaction or neither SHALL commit
- **AND** retrying settlement SHALL NOT create a duplicate handler row
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
- **THEN** the event SHALL be re-dispatched only to the failing handler recorded by that dead-letter row
- **AND** already-successful sibling handlers SHALL NOT be invoked again
- **AND** recovery state SHALL be persisted before invoking the handler
- **AND** a successful re-delivery SHALL mark the row resolved
- **AND** a persistence failure after handler success SHALL leave an explicit ambiguous redelivery state rather than report false success

### Requirement: Dead-letter operator access is authenticated, local by default, and redacted

Dead-letter list and re-delivery operations SHALL only be mapped on a loopback listener and SHALL require an operator credential. The default local credential SHALL be stored outside the API and supplied by the `mo` CLI; network addresses and forwarding headers SHALL NOT be treated as proof of operator identity. List and re-delivery responses SHALL expose only bounded, stack-free diagnostic summaries.

#### Scenario: Remote caller cannot inspect or replay

- **WHEN** a caller without the operator credential requests a dead-letter list or re-delivery
- **THEN** the server SHALL reject the request
- **AND** no handler side effect SHALL run

#### Scenario: Reverse proxy cannot expose local operator routes

- **WHEN** a caller reaches the loopback listener through a reverse proxy without the operator credential
- **THEN** the server SHALL reject the request
- **AND** no handler side effect SHALL run

#### Scenario: Local operator receives redacted diagnostics

- **WHEN** an authenticated operator lists unresolved dead letters
- **THEN** the event and summary error SHALL be returned
- **AND** the raw server exception stack, stack frames, and file paths SHALL NOT be returned

### Requirement: Dead-letter recovery has an operator surface

Dead-letter query and re-delivery SHALL be available through the server API and the `mo` CLI; internal store or grain methods alone do not satisfy the operator contract.

#### Scenario: Operator lists and re-delivers through mo

- **WHEN** an operator runs `mo event dead-letter list`
- **THEN** the CLI SHALL authenticate with the local operator credential
- **AND** unresolved dead-letter rows SHALL be displayed with recovery status and MAY be filtered by failing handler
- **WHEN** the operator runs `mo event dead-letter redeliver <id>`
- **THEN** the CLI SHALL authenticate with the local operator credential
- **AND** the corresponding API recovery operation SHALL run and report whether delivery succeeded
