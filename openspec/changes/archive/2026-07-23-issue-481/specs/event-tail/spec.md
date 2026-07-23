### Requirement: event tail resolves under the singular event noun

`mo event tail` SHALL resolve as the realtime event-envelope stream command. The command SHALL NOT be reachable through a plural `events` noun.

#### Scenario: Singular noun resolves

- **WHEN** a caller runs `mo event tail`
- **THEN** the command SHALL resolve and open the project event stream
- **AND** it SHALL NOT exit as an unknown command

#### Scenario: Plural noun does not resolve

- **WHEN** a caller runs `mo events tail`
- **THEN** the command SHALL NOT resolve
- **AND** it SHALL exit non-zero without opening a stream

### Requirement: event tail emits only envelopes from subscription establishment

`mo event tail` SHALL emit event envelopes that arrive after the subscription is established. The command SHALL stream one JSON object per line (NDJSON) and SHALL NOT wrap the stream in a JSON array or any other enclosing structure.

#### Scenario: Streaming live envelopes

- **WHEN** the project emits two envelopes after the subscription opens
- **THEN** the command SHALL write one JSON object per line to stdout
- **AND** each emitted line SHALL be a complete envelope
- **AND** the output SHALL NOT be wrapped in an array

#### Scenario: Envelopes before subscription are not replayed

- **WHEN** envelopes existed before the subscription was established
- **THEN** the command SHALL NOT emit those prior envelopes as part of the tail
- **AND** it SHALL emit only envelopes that arrive after subscription establishment

### Requirement: event tail forwards the match expression to the server

`mo event tail` SHALL forward an optional `--match` expression to the server, which is the single compile authority for that expression. The command SHALL NOT evaluate the expression locally. When the server reports the expression is invalid, the command SHALL print the server-supplied diagnostic to stderr and SHALL NOT emit any envelope.

#### Scenario: Valid match filters the stream

- **WHEN** a caller runs `mo event tail --match <expression>` and the server accepts the expression
- **THEN** the command SHALL forward the expression to the server
- **AND** it SHALL emit only envelopes that satisfy the expression

#### Scenario: Invalid match is reported without streaming

- **WHEN** the server rejects a `--match` expression
- **THEN** the command SHALL print the server's diagnostic (including line and column where supplied) to stderr
- **AND** it SHALL emit nothing to stdout
- **AND** it SHALL exit non-zero

### Requirement: event tail honors project scope resolution

`mo event tail` SHALL resolve exactly one project before opening the stream. An explicit `--project <name-or-id>` SHALL select that project; otherwise the active project selection SHALL be used. When no project can be uniquely resolved, the command SHALL fail locally and SHALL NOT contact the server.

#### Scenario: Explicit project overrides the active project

- **WHEN** a caller runs `mo event tail --project <name-or-id>`
- **THEN** the command SHALL open the stream scoped to that resolved project

#### Scenario: No active project

- **WHEN** a caller runs `mo event tail` with no `--project` and no resolvable active project
- **THEN** the command SHALL exit non-zero
- **AND** it SHALL NOT issue any HTTP request
- **AND** it SHALL report that no active project is selected

### Requirement: event tail stops cleanly on cancellation

When the caller interrupts `mo event tail`, the command SHALL stop emitting, release the in-flight request, and exit `130`.

#### Scenario: Caller interrupts the stream

- **WHEN** the caller interrupts a running `mo event tail`
- **THEN** the command SHALL stop emitting envelopes
- **AND** it SHALL release the underlying request
- **AND** it SHALL exit `130`
