## ADDED Requirements

### Requirement: CLI provides mo issue feedback command group

The CLI SHALL expose a `mo issue feedback` command group with `list` and `show` subcommands for querying approval feedback records.

#### Scenario: Feedback subcommands appear in help

- **WHEN** the user runs `mo issue --help`
- **THEN** the output SHALL list `feedback` as a subcommand group
- **AND** `mo issue feedback --help` SHALL list `list` and `show` subcommands

#### Scenario: Feedback list command invoked

- **WHEN** the user runs `mo issue feedback list 42`
- **THEN** the CLI SHALL call `GET /api/issues/42/feedback`
- **AND** display the results in a formatted table

#### Scenario: Feedback show command invoked

- **WHEN** the user runs `mo issue feedback show 42 --feedback fb_123`
- **THEN** the CLI SHALL call `GET /api/issues/42/feedback/fb_123`
- **AND** display the feedback details

#### Scenario: Feedback commands require server

- **WHEN** the user runs any `mo issue feedback` command
- **AND** the server is not running
- **THEN** the CLI SHALL display "Server is not running. Start with: mo server start"
- **AND** exit with non-zero status

### Requirement: CLI feedback commands support JSON output for agent consumption

The `mo issue feedback list` and `mo issue feedback show` commands SHALL support `--output json` for machine-readable output suitable for agent consumption.

#### Scenario: Feedback list as JSON

- **WHEN** the user runs `mo issue feedback list 42 --output json`
- **THEN** the CLI SHALL output a valid JSON array
- **AND** each element SHALL match the stable feedback JSON schema

#### Scenario: Feedback show as JSON

- **WHEN** the user runs `mo issue feedback show 42 --feedback fb_123 --output json`
- **THEN** the CLI SHALL output a valid JSON object
- **AND** the object SHALL match the stable feedback JSON schema

#### Scenario: JSON output omits formatting

- **WHEN** `--output json` is used
- **THEN** the CLI SHALL NOT include table borders, color codes, or other terminal formatting
- **AND** the output SHALL be parseable by standard JSON parsers

### Requirement: CLI feedback commands support stage filtering

The `mo issue feedback` commands SHALL support `--stage` filtering to scope results to a specific workflow stage.

#### Scenario: List feedback filtered by stage

- **WHEN** the user runs `mo issue feedback list 42 --stage plan`
- **THEN** the CLI SHALL call the API with `?stage=plan`
- **AND** only feedback records for the `plan` stage SHALL be displayed

#### Scenario: Show latest feedback for a stage

- **WHEN** the user runs `mo issue feedback show 42 --latest --stage build`
- **THEN** the CLI SHALL retrieve the most recent feedback for the `build` stage
- **AND** display the result

### Requirement: CLI feedback commands support --latest flag

The `mo issue feedback show` command SHALL support `--latest` to retrieve the most recently created feedback record without specifying a feedback id.

#### Scenario: Show latest feedback

- **WHEN** the user runs `mo issue feedback show 42 --latest`
- **THEN** the CLI SHALL retrieve the most recently created feedback record for the issue
- **AND** display the result

#### Scenario: Show latest with stage filter

- **WHEN** the user runs `mo issue feedback show 42 --latest --stage plan`
- **THEN** the CLI SHALL retrieve the most recently created feedback record for `plan` stage
- **AND** display the result

### Requirement: CLI feedback commands support explicit project id

The `mo issue feedback` commands SHALL accept `--project-id` for explicit project targeting, and SHALL use the current project context when omitted.

#### Scenario: Explicit project id

- **WHEN** the user runs `mo issue feedback list 42 --project-id proj_abc`
- **THEN** the CLI SHALL use `proj_abc` as the project context for the API call

#### Scenario: Current project context used by default

- **WHEN** the user runs `mo issue feedback list 42` without `--project-id`
- **THEN** the CLI SHALL use the current project context
