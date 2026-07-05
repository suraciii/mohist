## ADDED Requirements

### Requirement: CLI provides `mo project workflow profile list` command

The CLI SHALL provide a `mo project workflow profile list` command that lists all available workflow profiles with their name and description. The command SHALL be a thin client that calls the server API and SHALL NOT contain business logic. The old `mo workflow list` path is migrated away so the top-level `mo workflow` group can address WorkflowRun resources directly.

#### Scenario: List profiles in human-readable format
- **WHEN** the user runs `mo project workflow profile list`
- **THEN** the command SHALL display each profile's display name and description
- **AND** the default profile SHALL be marked with a visual indicator (e.g., `(default)`)
- **AND** multi-line descriptions SHALL be rendered with preserved formatting

#### Scenario: List profiles as JSON through the shared output option
- **WHEN** the user runs `mo project workflow profile list -o json`
- **THEN** the command SHALL output the server response as JSON
- **AND** each object SHALL include the fields returned by the profile-list endpoint
- **AND** the command SHALL also accept the long form `--output json`
- **AND** the command SHALL NOT define a separate `--json` flag

#### Scenario: Server is not running
- **WHEN** the user runs `mo project workflow profile list`
- **AND** the Mohist server is not running
- **THEN** the command SHALL output the standard server-not-running error
- **AND** exit with a non-zero status code

#### Scenario: Thin client pattern preserved
- **WHEN** `mo project workflow profile list` executes
- **THEN** the CLI SHALL call a server API endpoint to retrieve profile data
- **AND** the CLI SHALL NOT parse YAML files or compute metadata locally

### Requirement: Server API endpoint serves profile list

The server SHALL expose an API endpoint that returns the full list of available workflow profiles with their metadata, suitable for consumption by both the CLI and external agent skills.

#### Scenario: GET workflow profiles list
- **WHEN** the server receives a GET request to the profile list endpoint
- **THEN** the response SHALL be a JSON array of profile objects
- **AND** each object SHALL contain `id`, `displayName`, `description`, and `isDefault`

#### Scenario: Empty profile list is impossible
- **WHEN** the server receives a GET request to the profile list endpoint
- **THEN** the response SHALL always include at least the `mohist/local` profile
- **AND** the response SHALL NOT be an empty array

### Requirement: JSON output is consumable by external agent skills

The shared `-o json` / `--output json` output format SHALL produce valid JSON that external agent skills can parse to read profile data without additional tool calls.

#### Scenario: JSON is valid and self-contained
- **WHEN** an external agent runs `mo project workflow profile list -o json`
- **THEN** stdout SHALL contain only valid JSON
- **AND** each profile object SHALL contain the fields returned by the profile-list endpoint
- **AND** the JSON SHALL NOT include transient or environment-specific data
