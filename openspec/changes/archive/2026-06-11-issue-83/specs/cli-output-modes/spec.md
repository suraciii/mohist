## ADDED Requirements

### Requirement: `--output table|json` is available on high-use list/detail commands

A shared `--output <mode>` option SHALL be exposed on the following high-use commands: `mo project list`, `mo project show`, `mo issue list`, `mo issue show`, `mo issue workflow status`, and `mo issue sessions`. The accepted values SHALL be `table` and `json`. The default value SHALL be `json` so that existing automation keeps working unchanged.

#### Scenario: Project list accepts `--output table`
- **WHEN** the user runs `mo project list --output table`
- **THEN** the CLI renders the project list as a columnar human-readable table
- **AND** the underlying API request is identical to `mo project list`

#### Scenario: Issue list accepts `--output table`
- **WHEN** the user runs `mo issue list --output table`
- **THEN** the CLI renders the issue list as a columnar human-readable table
- **AND** the underlying API request is identical to `mo issue list`

#### Scenario: Default is JSON for automation stability
- **WHEN** the user runs `mo issue list` with no `--output` flag
- **THEN** the CLI SHALL output JSON (the existing behavior)
- **AND** existing scripts that consume the JSON output SHALL continue to work unchanged

#### Scenario: `--output json` matches default
- **WHEN** the user runs `mo issue list --output json`
- **THEN** the CLI SHALL output the same JSON as the no-flag invocation
- **AND** the underlying API request SHALL be identical

#### Scenario: Unknown output value fails with a clear error
- **WHEN** the user runs `mo issue list --output yaml`
- **THEN** the CLI SHALL print a clear validation error listing the accepted values (`table`, `json`)
- **AND** SHALL exit with a non-zero status
- **AND** SHALL NOT make a server request

### Requirement: Table output renders a readable human summary

When `--output table` is selected, the CLI SHALL render a columnar human-readable summary of the response. The rendered table SHALL include the most useful identifiers and at least one state column for list commands, and SHALL truncate long text fields to a reasonable terminal width.

#### Scenario: Project list table includes id, name, and base branch
- **WHEN** the user runs `mo project list --output table`
- **THEN** the rendered table SHALL include project id, project name, and base branch columns
- **AND** the active project SHALL be marked (e.g. with `*`) when the active project state is set

#### Scenario: Issue list table includes number, title, stage, status, and priority
- **WHEN** the user runs `mo issue list --output table`
- **THEN** the rendered table SHALL include issue number, title, stage, status, and priority columns
- **AND** long titles SHALL be truncated to a reasonable terminal width

#### Scenario: Issue show table is a multi-line summary
- **WHEN** the user runs `mo issue show 83 --output table`
- **THEN** the CLI SHALL render a multi-line human-readable summary
- **AND** the summary SHALL include number, title, stage, status, priority, project, and updated time
- **AND** the body SHALL be either omitted or rendered in a condensed form (not a full Markdown dump)

#### Scenario: Workflow status table summarizes stages and tasks
- **WHEN** the user runs `mo issue workflow status 83 --output table`
- **THEN** the CLI SHALL render a table summarizing the current stage, task states, and any waiting reasons
- **AND** the underlying API request SHALL be identical to the no-flag invocation

#### Scenario: Sessions table lists session id, state, started, and model
- **WHEN** the user runs `mo issue sessions 83 --output table`
- **THEN** the CLI SHALL render a table summarizing each session with id, current call state, started time, and model
- **AND** the underlying API request SHALL be identical to the no-flag invocation

### Requirement: Table rendering does not change the underlying request

The `--output table` mode SHALL NOT change the API request, request parameters, or authentication. It SHALL be a presentation-time concern only. The same data SHALL be returned by the server in both `table` and `json` modes for a given command.

#### Scenario: Table and json hit the same endpoint
- **WHEN** the user runs `mo issue list --output table`
- **AND** runs `mo issue list --output json`
- **THEN** both invocations SHALL send the same HTTP request to the same server endpoint
- **AND** the request method, path, query string, and body SHALL be identical

#### Scenario: Table rendering does not require extra server round-trips
- **WHEN** the user runs `mo issue show 83 --output table`
- **THEN** the CLI SHALL make exactly one server request
- **AND** the response SHALL be the same `data` payload that `--output json` would render
