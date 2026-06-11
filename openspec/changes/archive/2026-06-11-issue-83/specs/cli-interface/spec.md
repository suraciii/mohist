## ADDED Requirements

### Requirement: Project-scoped issue subcommands accept `--project <name-or-id>` and keep `--project-id` as a compatibility alias

The CLI SHALL expose a canonical `--project <name-or-id>` option on every project-scoped `mo issue` subcommand (list, show, create, update, start, approve, reject, close, reopen, retry, rerun, force-stop, resume, rebase, archive, unarchive, logs, events, diff, commits, sessions, workflow status, workflow timeline). The CLI SHALL continue to accept `--project-id` as a backwards-compatible alias. The full semantics (name/id resolution, conflict validation, active project fallback, "no active project" diagnostic) are defined in the `cli-project-ref` capability. This requirement anchors the option surface in `cli-interface`.

#### Scenario: Option is documented on each issue subcommand
- **WHEN** the user runs `mo <subcommand> --help` for any project-scoped `mo issue` subcommand
- **THEN** the help output SHALL list `--project` as a documented option
- **AND** SHALL list `--project-id` as a backwards-compatible alias

#### Scenario: Backwards compatibility is preserved
- **WHEN** existing scripts use `--project-id` on any project-scoped `mo issue` subcommand
- **THEN** those scripts SHALL continue to work unchanged
- **AND** SHALL resolve the project id through the same shared helper as `--project`

### Requirement: Project-scoped list/detail commands accept `--output table|json`

The CLI SHALL expose a shared `--output <mode>` option on `mo project list`, `mo project show`, `mo issue list`, `mo issue show`, `mo issue workflow status`, and `mo issue sessions`. The accepted values SHALL be `table` and `json`. The default SHALL be `json` to preserve existing automation. The full semantics (table column shape, validation error, request equivalence) are defined in the `cli-output-modes` capability. This requirement anchors the option surface in `cli-interface`.

#### Scenario: Option is documented on each supported command
- **WHEN** the user runs `mo <command> --help` for any of the supported commands
- **THEN** the help output SHALL list `--output <mode>` with accepted values `table` and `json`
- **AND** SHALL note that the default is `json`

#### Scenario: Default remains JSON
- **WHEN** the user runs a supported command without `--output`
- **THEN** the CLI SHALL output JSON (the existing behavior)
- **AND** existing automation that consumes the JSON output SHALL continue to work unchanged

### Requirement: `issue create` and `issue update` expose first-class `--body-stdin` and `--body-file` options

The CLI SHALL expose `--body-file <path>` and `--body-stdin` as first-class options on `mo issue create` and `mo issue update`, in addition to the existing inline `--body <body>`. The three sources SHALL be mutually exclusive. The full semantics (file-read behavior, stdin drain, validation error wording, exit codes) are defined in the `cli-body-input-sources` capability. This requirement anchors the option surface in `cli-interface`.

#### Scenario: New options are documented in help
- **WHEN** the user runs `mo issue create --help` or `mo issue update --help`
- **THEN** the help output SHALL list `--body`, `--body-file <path>`, and `--body-stdin`
- **AND** SHALL note that the three sources are mutually exclusive

#### Scenario: New options default off
- **WHEN** the user runs `mo issue create "Title" --body "literal"` without `--body-file` or `--body-stdin`
- **THEN** the CLI SHALL send the literal body unchanged
- **AND** SHALL NOT read any file or stdin

### Requirement: `mo project repo` subcommand group is exposed

The CLI SHALL expose a `mo project repo` subcommand group with `list`, `add`, `set-default`, and `remove` subcommands. The subcommand group SHALL wrap the existing server endpoints at `/api/projects/{ref}/repositories` and SHALL NOT introduce new server semantics. The full request paths and error surface are defined in the `cli-project-repositories` capability. This requirement anchors the option surface in `cli-interface`.

#### Scenario: Subcommand group is listed in `mo project --help`
- **WHEN** the user runs `mo project --help`
- **THEN** the help output SHALL list `repo` alongside the existing subcommands (list, create, show, use, delete)

#### Scenario: Subcommands are listed in `mo project repo --help`
- **WHEN** the user runs `mo project repo --help`
- **THEN** the help output SHALL list `list`, `add`, `set-default`, and `remove`

#### Scenario: CLI does not introduce new server semantics
- **WHEN** the user runs any `mo project repo` subcommand
- **THEN** the CLI SHALL send requests to existing `/api/projects/{ref}/repositories` endpoints
- **AND** the CLI SHALL NOT introduce a new server route, schema, or grain method

### Requirement: Standardized "no active project" diagnostic references the canonical `--project` option

The "no active project" error surfaced by any project-scoped CLI subcommand (including the new `mo project repo` subcommands) SHALL mention both remediation options: setting an active project via `mo project use` and passing `--project` on the failing command. The error SHALL mention `--project <name-or-id>` (not `--project-id`) so users learn the canonical option.

#### Scenario: Diagnostic references `--project <name-or-id>`
- **WHEN** a project-scoped CLI subcommand fails because no active project is set and no project option is passed
- **THEN** the CLI error message SHALL mention `mo project use <name-or-id>`
- **AND** SHALL mention `pass --project <name-or-id>`
- **AND** SHALL NOT mention `--project-id` in the diagnostic

#### Scenario: Diagnostic wording is consistent across commands
- **WHEN** the user triggers the "no active project" path on `mo issue show`, `mo project repo list`, or any other project-scoped subcommand
- **THEN** all such errors SHALL use the same remediation wording
- **AND** the wording SHALL be rendered by a single shared helper
