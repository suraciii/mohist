### Requirement: Query execution is bounded by a mechanism that actually interrupts SQLite work

`POST /otel/api/query` SHALL bound query execution by a cancellation mechanism that interrupts the SQLite reader's actual work, not by relying on `CommandTimeout` as a long-query execution timeout. `CommandTimeout` MAY remain as a defense-in-depth cap on SQLite lock-wait but SHALL NOT be the primary mechanism that stops a long-running SELECT. When the execution budget is exhausted, the in-flight reader SHALL be cancelled and the connection released, not merely the HTTP wait.

#### Scenario: Exhausting the execution budget interrupts the running reader

- **WHEN** a query runs longer than the execution budget
- **THEN** the SQLite reader SHALL be cancelled via the execution-budget cancellation mechanism
- **AND** the read-only connection SHALL be released rather than held until the HTTP layer gives up
- **AND** the interruption SHALL be observable at the reader level, not only at the HTTP response level

#### Scenario: CommandTimeout is not relied on as the long-query execution budget

- **WHEN** the execution-budget mechanism is examined
- **THEN** it SHALL be independent of `CommandTimeout`
- **AND** `CommandTimeout` SHALL remain only as a lock-wait defense-in-depth cap, not the primary long-query bound

### Requirement: Client cancellation interrupts SQLite execution and releases the connection

When the HTTP request is aborted by the client (the request-aborted cancellation token fires), the endpoint SHALL cancel the in-flight SQLite reader and release the read-only connection. The endpoint SHALL NOT continue consuming rows or hold the connection open until some other timeout expires.

#### Scenario: Client disconnect cancels the reader and releases the connection

- **WHEN** the client aborts the HTTP request while a query is reading rows
- **THEN** the endpoint SHALL cancel the SQLite reader via the request's cancellation token
- **AND** SHALL release the read-only connection
- **AND** SHALL NOT continue reading rows after the abort

#### Scenario: Cancellation behavior is verifiable without wall-clock timing

- **WHEN** a test exercises execution-budget or client-cancellation behavior
- **THEN** the cancellation SHALL be driven by an explicit cancellation token or an injected control seam
- **AND** the test SHALL NOT assert on real elapsed wall-clock duration

### Requirement: Execution-budget exhaustion returns a structured rejection rather than a partial result

When the execution budget is exhausted mid-query (as distinct from client cancellation), the endpoint SHALL return a structured error response with a stable code identifying execution-budget exhaustion. The endpoint SHALL NOT return a partial row array presented as a complete result, and SHALL NOT leave the response hanging.

#### Scenario: Execution-budget exhaustion produces a structured rejection

- **WHEN** the execution budget is exhausted while a query is running
- **THEN** the endpoint SHALL return a response carrying a stable error code identifying execution-budget exhaustion
- **AND** SHALL NOT return a partial row array presented as a complete result
- **AND** SHALL release the read-only connection
