### Requirement: Resource leaf commands declare discoverable JSON fields

Every leaf command that returns a resource or collection of resources SHALL declare the fields it can return through `--json [<fields>]`. Bare `--json` SHALL write one JSON array of the command's declared field-name strings to stdout, in that descriptor's declared order, and exit successfully without resolving a Project or contacting a Mohist service, Server, or Runner.

#### Scenario: Field discovery is requested

- **WHEN** an operator invokes a resource leaf command with bare `--json`
- **THEN** the command SHALL write one JSON array containing its declared field-name strings in descriptor order to stdout and exit with code `0`
- **AND** SHALL NOT make a remote request or require a Project selection

### Requirement: Field selection produces a projection

`--json <fields>` SHALL accept a comma-separated list of fields declared by the invoked resource leaf command. On success, the CLI SHALL emit only the selected fields of each returned resource. An unknown, duplicate, blank, or otherwise invalid field selection SHALL be rejected locally as a usage error and MUST NOT issue a remote request.

#### Scenario: A single resource is projected

- **WHEN** an operator invokes a single-resource read with `--json number,title`
- **THEN** stdout SHALL contain one JSON object containing only `number` and `title` from the returned resource

#### Scenario: An unknown field is requested

- **WHEN** an operator invokes a resource leaf command with a field not declared by that command
- **THEN** the CLI SHALL write a diagnostic naming the invalid field and the command's field-discovery invocation to stderr
- **AND** SHALL exit with code `2`
- **AND** SHALL NOT make a remote request

### Requirement: Successful machine output has a stable cardinality shape

A successful selected-field read of one resource SHALL emit one JSON object. A successful selected-field read of a collection SHALL emit one JSON array whose elements are resource objects. A successful continuous event or log command SHALL emit newline-delimited JSON, with one complete JSON object per line. Successful output MUST NOT use a general `{ ok, data, error }` envelope.

#### Scenario: A collection is projected

- **WHEN** an operator invokes a collection read with `--json number,title`
- **THEN** stdout SHALL contain one JSON array
- **AND** every array element SHALL contain only `number` and `title`

#### Scenario: A continuous stream emits NDJSON

- **WHEN** a continuous event or log command emits two successful records
- **THEN** stdout SHALL contain two newline-delimited JSON objects
- **AND** stdout SHALL NOT contain an enclosing JSON array or response envelope

### Requirement: Results and diagnostics use separate streams

Successful result data, including JSON and NDJSON, SHALL be written only to stdout. Errors, hints, confirmations, and progress messages SHALL be written only to stderr. Human-oriented rendering MUST NOT alter the JSON object, array, or NDJSON contract selected by `--json <fields>`.

#### Scenario: A successful read also has progress information

- **WHEN** a resource command produces selected-field JSON and progress information
- **THEN** stdout SHALL contain only the JSON result
- **AND** the progress information SHALL be written to stderr

#### Scenario: A read fails after JSON was requested

- **WHEN** a resource command invoked with `--json <fields>` fails
- **THEN** stdout SHALL contain no diagnostic text or error envelope
- **AND** the diagnostic SHALL be written to stderr

### Requirement: Legacy output selectors are absent from resource reads

Resource leaf commands SHALL use `--json [<fields>]` as their machine-readable output interface. Legacy `--output` selectors and boolean JSON output modes MUST NOT resolve for those commands.

#### Scenario: A legacy output selector is supplied

- **WHEN** an operator invokes a resource leaf command with `--output json`
- **THEN** the CLI SHALL reject the option as a usage error without contacting a Mohist service
- **AND** SHALL exit with code `2`
