### Requirement: activity list returns a bounded persistent activity record

`mo activity list` SHALL return a finite, read-only view of the project's persistent activity records. The command SHALL NOT open a realtime subscription and SHALL NOT perform a delivery-recovery operation; it reads persisted history that exists independently of the command's lifetime.

#### Scenario: Reading a project's recent activity

- **WHEN** a caller runs `mo activity list` against a project that has activity records
- **THEN** the command SHALL return a bounded set of those records as normal output
- **AND** it SHALL exit `0`
- **AND** it SHALL NOT treat the read as a streaming tail that only moves forward

#### Scenario: Re-reading history after the command exits

- **WHEN** the caller runs `mo activity list` a second time against the same unchanged project
- **THEN** the command SHALL return the same persisted activity records as the prior read
- **AND** the first invocation SHALL NOT have consumed, advanced, or altered the history visible to the second

### Requirement: activity list honors project scope resolution

`mo activity list` SHALL resolve exactly one project before contacting the server. An explicit `--project <name-or-id>` SHALL select that project; otherwise the active project selection SHALL be used. When no project can be uniquely resolved, the command SHALL fail locally and SHALL NOT contact the server.

#### Scenario: Explicit project overrides the active project

- **WHEN** a caller runs `mo activity list --project <name-or-id>`
- **THEN** the command SHALL read activity scoped to that resolved project
- **AND** it SHALL NOT read activity for any other project

#### Scenario: No active project

- **WHEN** a caller runs `mo activity list` with no `--project` and no resolvable active project
- **THEN** the command SHALL exit non-zero
- **AND** it SHALL NOT issue any HTTP request
- **AND** it SHALL report that no active project is selected with an actionable hint

### Requirement: activity list supports field selection

`mo activity list` SHALL support the shared field-selection contract: `--json <fields>` SHALL output only the requested fields as a JSON array of records, field order SHALL NOT affect semantics, and `--json` supplied with no fields SHALL list the command's selectable fields and exit without reading records.

#### Scenario: Selecting a subset of fields

- **WHEN** a caller runs `mo activity list --json <comma-separated-fields>`
- **THEN** the command SHALL emit one JSON array containing only the requested fields per record
- **AND** it SHALL NOT emit fields the caller did not request

#### Scenario: Field discovery

- **WHEN** a caller runs `mo activity list --json` with no field list
- **THEN** the command SHALL enumerate the selectable fields and exit
- **AND** it SHALL NOT require the caller to guess field names

### Requirement: activity list is bounded by a caller-controlled limit

`mo activity list` SHALL bound its result so the read terminates. The command SHALL accept a caller-controlled limit and SHALL reject a limit outside its declared valid range before contacting the server.

#### Scenario: Limit within range

- **WHEN** a caller runs `mo activity list` with a limit inside the valid range
- **THEN** the command SHALL request at most that many records
- **AND** it SHALL exit `0` when records are returned

#### Scenario: Limit outside range

- **WHEN** a caller runs `mo activity list` with a limit outside the valid range
- **THEN** the command SHALL exit non-zero
- **AND** it SHALL NOT contact the server
- **AND** it SHALL report the valid range on stderr
