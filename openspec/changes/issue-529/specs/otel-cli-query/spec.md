### Requirement: otel query executes SQL through the Server query surface

`mo otel query <sql>` SHALL submit the SQL statement to the Server query endpoint and render the Server's response. The command MUST NOT open, read, or resolve any local database file, and MUST NOT provide a way to select a local database path. Admission, read-only enforcement, execution budget, and response bounds are owned by the Server; the CLI SHALL NOT re-implement or bypass them.

#### Scenario: A successful query renders rows

- **WHEN** the Server returns a complete (non-truncated) result for a SELECT
- **THEN** the command SHALL render the column headers and rows to stdout
- **AND** SHALL exit with code 0

#### Scenario: The SQL argument is omitted

- **WHEN** `mo otel query` is invoked without a SQL argument
- **THEN** the command SHALL write a diagnostic naming the required SQL argument to stderr
- **AND** SHALL exit with a non-zero code
- **AND** SHALL NOT contact the Server

### Requirement: A local database path is not accepted

The command MUST NOT accept a `--db` option or any other mechanism that selects a local storage path. Querying local storage instead of the configured Server target is not a supported product path.

#### Scenario: A local database option is supplied

- **WHEN** `mo otel query <sql> --db <path>` is invoked
- **THEN** the command SHALL reject `--db` as an unknown option
- **AND** SHALL exit with a non-zero code
- **AND** SHALL NOT open the supplied path or contact the Server

### Requirement: Truncation is surfaced to the caller

When the Server response carries a truncation indicator, the command SHALL surface both the truncation state and its reason, so a human or Agent can tell the result was bounded and which bound was hit. A non-truncated response SHALL NOT present truncation.

#### Scenario: A truncated result renders the truncation reason

- **WHEN** the Server returns a result marked truncated with a reason
- **THEN** the human-readable output SHALL include the rows returned so far
- **AND** SHALL include a truncation notice naming the reason

#### Scenario: A non-truncated result renders no truncation

- **WHEN** the Server returns a complete result without a truncation indicator
- **THEN** the output SHALL NOT contain a truncation notice

### Requirement: otel query supports JSON field selection

`mo otel query` SHALL support the shared field-selection contract. Bare `--json` SHALL list the command's selectable fields and exit without contacting the Server. `--json <fields>` SHALL emit only the requested fields from the query result. The selectable fields SHALL include `rows`, `truncated`, and `truncate_reason`. An invalid field SHALL be rejected locally as a usage error without issuing a remote request.

#### Scenario: Field discovery

- **WHEN** `mo otel query --json` is invoked with no field list
- **THEN** the command SHALL write one JSON array of its selectable field names to stdout and exit with code 0
- **AND** SHALL NOT contact the Server

#### Scenario: Selected projection

- **WHEN** `mo otel query <sql> --json rows,truncated` is invoked and the Server returns a result
- **THEN** stdout SHALL contain only the requested fields from the query result
- **AND** SHALL NOT include `truncate_reason` when it was not selected

#### Scenario: An unknown field is rejected

- **WHEN** `mo otel query <sql> --json nonexistent` is invoked
- **THEN** the command SHALL write a diagnostic naming the invalid field to stderr and exit with code 2
- **AND** SHALL NOT contact the Server

### Requirement: Server unavailability and query errors are actionable

When the Server is unreachable, the command SHALL fail with the standard Server-unavailable diagnostic rather than falling back to local storage. When the Server rejects the query (malformed SQL, non-SELECT statement, exhausted execution budget, or SQLite engine error), the command SHALL surface the Server's error to the caller and exit with a non-zero code.

#### Scenario: Server is not running

- **WHEN** the configured Server is unreachable
- **THEN** the command SHALL write the standard Server-unavailable message to stderr
- **AND** SHALL exit with a non-zero code
- **AND** SHALL NOT open any local database

#### Scenario: The Server rejects the SQL

- **WHEN** the Server returns an error for the submitted SQL
- **THEN** the command SHALL surface the Server's error message to stderr and exit with a non-zero code
