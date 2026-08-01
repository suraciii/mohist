### Requirement: otel traces lists recent traces through the Server

`mo otel traces` SHALL submit a request to the Server's `GET /otel/api/traces` capability and render the returned trace summaries. The command MUST NOT open, read, or resolve any local database file. Filtering, ordering, and result bounding are owned by the Server; the CLI SHALL NOT re-implement them.

#### Scenario: A populated list renders the recent traces

- **WHEN** the Server returns a non-empty list of trace summaries
- **THEN** the command SHALL render those traces to stdout in the order the Server returns them (most-recent first)
- **AND** SHALL exit with code 0

#### Scenario: The list never touches local storage

- **WHEN** `mo otel traces` is invoked
- **THEN** the command SHALL NOT open any local database file
- **AND** SHALL obtain the list solely from the configured Server target

### Requirement: service and limit filters are forwarded to the Server

The command SHALL accept a `--service <name>` option that restricts results to a single service and a `--limit <n>` option that sets the requested maximum number of traces. Both filters SHALL be forwarded to the Server as request parameters. The Server's result bounding (its default limit and hard cap) SHALL be authoritative; the CLI MUST NOT impose its own limit policy. When neither option is supplied, the command SHALL request the Server's default recent-traces view.

#### Scenario: A service filter is applied

- **WHEN** `mo otel traces --service <name>` is invoked and the Server returns matching traces
- **THEN** the rendered traces SHALL all belong to the named service
- **AND** the command SHALL forward the service filter to the Server

#### Scenario: A limit is applied

- **WHEN** `mo otel traces --limit <n>` is invoked
- **THEN** the command SHALL forward the requested limit to the Server as a request parameter
- **AND** SHALL NOT impose a local upper bound on the count that is tighter than the Server's own bound

### Requirement: otel traces supports JSON field selection

`mo otel traces` SHALL support the shared field-selection contract. Bare `--json` SHALL list the command's selectable fields and exit without contacting the Server. `--json <fields>` SHALL emit only the requested fields from each trace summary. The selectable fields SHALL include `trace_id`, `service_name`, `start_time`, `end_time`, and `span_count`. An invalid field SHALL be rejected locally as a usage error without issuing a remote request.

#### Scenario: Field discovery

- **WHEN** `mo otel traces --json` is invoked with no field list
- **THEN** the command SHALL write one JSON array of its selectable field names to stdout and exit with code 0
- **AND** SHALL NOT contact the Server

#### Scenario: Selected projection

- **WHEN** `mo otel traces --service <name> --json trace_id,span_count` is invoked and the Server returns traces
- **THEN** stdout SHALL contain only the requested fields from each trace summary
- **AND** SHALL NOT include a field that was not selected

#### Scenario: An unknown field is rejected

- **WHEN** `mo otel traces --json nonexistent` is invoked
- **THEN** the command SHALL write a diagnostic naming the invalid field to stderr and exit with a non-zero code
- **AND** SHALL NOT contact the Server

### Requirement: human-readable output is a compact table

When no `--json` selection is provided, the command SHALL render the trace summaries as a compact table to stdout. An empty result SHALL be reported clearly rather than rendered as a bare header.

#### Scenario: A table is rendered by default

- **WHEN** `mo otel traces` is invoked without `--json` and the Server returns traces
- **THEN** stdout SHALL present the trace summaries as a column-aligned table
- **AND** SHALL NOT emit JSON

#### Scenario: An empty list is reported

- **WHEN** `mo otel traces` is invoked and the Server returns no traces
- **THEN** the output SHALL indicate that zero traces were returned
- **AND** SHALL exit with code 0

### Requirement: Server unavailability is actionable

When the configured Server is unreachable, the command SHALL fail with the standard Server-unavailable diagnostic on stderr and exit with a non-zero code, rather than falling back to local storage or emitting an empty list.

#### Scenario: Server is not running

- **WHEN** the configured Server is unreachable
- **THEN** the command SHALL write the standard Server-unavailable message to stderr
- **AND** SHALL exit with a non-zero code
- **AND** SHALL NOT open any local database

### Requirement: leaf help positions traces against query

The `mo otel traces` leaf help SHALL describe the command's purpose and SHALL state the division of labor with `mo otel query`: `traces` for typed browsing of recent traces, `query` for free-SQL exploration.

#### Scenario: Leaf help explains the split

- **WHEN** `mo otel traces --help` is invoked
- **THEN** the help text SHALL name the `--service` and `--limit` options
- **AND** SHALL reference `mo otel query` to distinguish typed browsing from free-SQL exploration
