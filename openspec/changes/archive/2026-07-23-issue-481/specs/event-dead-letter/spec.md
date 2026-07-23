### Requirement: dead-letter commands resolve under the singular event noun

`mo event dead-letter list` and `mo event dead-letter redeliver` SHALL resolve under the singular `event` noun. They SHALL NOT be reachable through a plural `events` noun.

#### Scenario: Singular list resolves

- **WHEN** a caller runs `mo event dead-letter list`
- **THEN** the command SHALL resolve and request the current failed deliveries
- **AND** it SHALL NOT exit as an unknown command

#### Scenario: Singular redeliver resolves

- **WHEN** a caller runs `mo event dead-letter redeliver <id>`
- **THEN** the command SHALL resolve and perform the redelivery
- **AND** it SHALL NOT exit as an unknown command

### Requirement: dead-letter list returns current failed deliveries

`mo event dead-letter list` SHALL return the currently failed (unresolved) event deliveries. The command SHALL accept a caller-controlled limit bounded by a declared valid range and a handler filter, and SHALL reject a limit outside the valid range before contacting the server.

#### Scenario: Listing current failures

- **WHEN** a caller runs `mo event dead-letter list`
- **THEN** the command SHALL return the current failed deliveries
- **AND** it SHALL exit `0` when deliveries are returned

#### Scenario: Filtering by handler

- **WHEN** a caller runs `mo event dead-letter list --handler <name>`
- **THEN** the command SHALL encode the handler filter and forward it to the server
- **AND** it SHALL scope the result to that handler

#### Scenario: Limit outside range

- **WHEN** a caller runs `mo event dead-letter list --limit <out-of-range>`
- **THEN** the command SHALL exit non-zero
- **AND** it SHALL NOT contact the server
- **AND** it SHALL report the valid range on stderr

### Requirement: redeliver retries an explicit target and reports a decidable result

`mo event dead-letter redeliver` SHALL retry a single, explicit dead-letter id and SHALL report a decidable outcome. The command SHALL reject a non-positive id before contacting the server. When the server reports failure, the command SHALL surface the server's reason and SHALL exit non-zero.

#### Scenario: Successful redelivery

- **WHEN** a caller runs `mo event dead-letter redeliver <id>` and the retry succeeds
- **THEN** the command SHALL report that the delivery succeeded
- **AND** it SHALL report the attempt count
- **AND** it SHALL exit `0`

#### Scenario: Failed redelivery

- **WHEN** the server reports the redelivery could not be completed
- **THEN** the command SHALL print the server's reason and stable error code to stderr
- **AND** it SHALL exit non-zero

#### Scenario: Non-positive id

- **WHEN** a caller runs `mo event dead-letter redeliver <id>` with a non-positive id
- **THEN** the command SHALL exit non-zero
- **AND** it SHALL NOT contact the server

### Requirement: dead-letter commands require the local operator credential

`mo event dead-letter list` and `redeliver` SHALL require a local operator credential supplied through the operator-credential resolution chain. When no credential is available, the command SHALL fail before contacting the server and SHALL report that the credential was not found.

#### Scenario: Credential present authenticates the request

- **WHEN** a caller runs a dead-letter command and a local operator credential is available
- **THEN** the command SHALL attach that credential to the request
- **AND** it SHALL proceed with the operation

#### Scenario: Credential missing

- **WHEN** a caller runs a dead-letter command and no operator credential is available
- **THEN** the command SHALL exit non-zero
- **AND** it SHALL NOT issue any HTTP request
- **AND** it SHALL report that the operator credential was not found

### Requirement: dead-letter commands require a loopback server URL

`mo event dead-letter list` and `redeliver` SHALL send the operator credential only to a loopback server URL. When the configured server URL is not loopback, the command SHALL fail before reading or sending the credential and SHALL NOT degrade to an anonymous request.

#### Scenario: Non-loopback server URL

- **WHEN** a caller runs a dead-letter command against a non-loopback server URL
- **THEN** the command SHALL exit non-zero
- **AND** it SHALL NOT send the operator credential
- **AND** it SHALL NOT issue the request as an anonymous caller
- **AND** it SHALL report that a loopback server URL is required

### Requirement: dead-letter table output neutralizes untrusted content

When `mo event dead-letter list` renders server-supplied cells in its human-readable table, it SHALL strip terminal-control sequences and SHALL NOT emit hidden control characters from untrusted cell values.

#### Scenario: Failure message contains terminal control sequences

- **WHEN** a returned delivery's error text contains terminal-control or carriage sequences
- **THEN** the rendered table SHALL display the visible text without those control sequences
- **AND** the output SHALL NOT contain the control characters
