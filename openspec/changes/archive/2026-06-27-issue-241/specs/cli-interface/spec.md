## ADDED Requirements

### Requirement: CLI provides mo issue session command group

The CLI SHALL provide `mo issue session` (singular) as a command group under `mo issue`, exposing exactly four subcommands: `show`, `transcript`, `compact`, and `reset`. The group SHALL be distinct from the existing `mo issue sessions` (plural, list) command, which SHALL remain unchanged and is the source of the `<name>` positional argument used by the new verbs. Each subcommand SHALL accept the issue number and session name positional arguments, the `--project` / `--project-id` project-reference options, and the `-o table|json` output option. `mo issue session --help` SHALL list the four subcommands and SHALL document that `<name>` comes from the `mo issue sessions <num>` listing.

#### Scenario: Help lists the four session subcommands

- **WHEN** the user runs `mo issue session --help`
- **THEN** the output SHALL list the subcommands `show`, `transcript`, `compact`, and `reset`

#### Scenario: All session subcommands accept project reference and output options

- **WHEN** the user runs any `mo issue session <subcommand> <num> <name>` with `--project <name>` (or `--project-id <id>`) and `-o json`
- **THEN** the CLI SHALL resolve the project and emit output in JSON

#### Scenario: Existing list command is preserved

- **WHEN** the user runs `mo issue sessions <num>`
- **THEN** the CLI SHALL behave identically to before this change
- **AND** SHALL list coder sessions for the issue

### Requirement: CLI issue session show returns session metadata

`mo issue session show <num> <name>` SHALL send `GET /api/projects/:projectId/issues/:number/sessions/:name` and render the returned session metadata. The rendered output SHALL surface the session name, status, model, created time, and usage information (message/part count, token estimate, context-window usage). The output SHALL support `-o table` (human-readable) and `-o json` (raw payload). When the session does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Show renders session metadata in table mode

- **WHEN** the user runs `mo issue session show 42 plan -o table`
- **THEN** the CLI sends `GET /api/projects/:projectId/issues/42/sessions/plan`
- **AND** the rendered output SHALL present the session status, model, created time, and usage information

#### Scenario: Show in JSON emits the raw payload

- **WHEN** the user runs `mo issue session show 42 plan -o json`
- **THEN** the CLI SHALL print the server response as JSON

#### Scenario: Show nonexistent session surfaces error

- **WHEN** the user runs `mo issue session show 42 missing`
- **AND** the server returns `404` for session `missing`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status

### Requirement: CLI issue session transcript returns summary or full transcript

`mo issue session transcript <num> <name>` SHALL send `GET /api/projects/:projectId/issues/:number/sessions/:name/transcript`. In `-o table` mode the CLI SHALL render a summary (turn count / part count, first and last activity timestamps) rather than dumping every message, because transcripts can be long. In `-o json` mode the CLI SHALL print the full transcript in its raw server JSON shape without omission or beautification beyond standard JSON formatting. When the session does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Transcript table mode renders a summary

- **WHEN** the user runs `mo issue session transcript 42 plan -o table`
- **THEN** the CLI sends `GET /api/projects/:projectId/issues/42/sessions/plan/transcript`
- **AND** the rendered output SHALL present a summary including the part count and first/last activity timestamps
- **AND** the output SHALL NOT dump every individual message body

#### Scenario: Transcript JSON mode emits the full transcript

- **WHEN** the user runs `mo issue session transcript 42 plan -o json`
- **THEN** the CLI SHALL print the full transcript payload as returned by the server, preserving all turns and parts

#### Scenario: Transcript nonexistent session surfaces error

- **WHEN** the user runs `mo issue session transcript 42 missing`
- **AND** the server returns `404` for session `missing`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status

### Requirement: CLI issue session compact reports new session identifier

`mo issue session compact <num> <name>` SHALL send `POST /api/projects/:projectId/issues/:number/sessions/:name/compact` and print the new follow-on session identifier as `New session: <id>` (sourced from the recovery result's `agentSessionId`) so an agent or user knows the subsequent session identifier. The output SHALL support `-o table` (human-readable, including the context-window before/after) and `-o json` (raw recovery payload). When the session does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Compact prints the new session identifier

- **WHEN** the user runs `mo issue session compact 42 plan`
- **AND** the server accepts the compaction
- **THEN** the CLI SHALL print `New session: <id>` using the recovery result's follow-on session identifier
- **AND** SHALL exit with status 0

#### Scenario: Compact in JSON emits the raw recovery payload

- **WHEN** the user runs `mo issue session compact 42 plan -o json`
- **THEN** the CLI SHALL print the full recovery payload as returned by the server

#### Scenario: Compact nonexistent session surfaces error

- **WHEN** the user runs `mo issue session compact 42 missing`
- **AND** the server returns `404` for session `missing`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status

### Requirement: CLI issue session reset reports new session identifier

`mo issue session reset <num> <name>` SHALL send `POST /api/projects/:projectId/issues/:number/sessions/:name/reset` and print the new follow-on session identifier as `New session: <id>` (sourced from the recovery result's `agentSessionId`), same shape as `compact`. The output SHALL support `-o table` (human-readable, including the context-window before/after) and `-o json` (raw recovery payload). When the session does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Reset prints the new session identifier

- **WHEN** the user runs `mo issue session reset 42 plan`
- **AND** the server accepts the reset
- **THEN** the CLI SHALL print `New session: <id>` using the recovery result's follow-on session identifier
- **AND** SHALL exit with status 0

#### Scenario: Reset in JSON emits the raw recovery payload

- **WHEN** the user runs `mo issue session reset 42 plan -o json`
- **THEN** the CLI SHALL print the full recovery payload as returned by the server

#### Scenario: Reset nonexistent session surfaces error

- **WHEN** the user runs `mo issue session reset 42 missing`
- **AND** the server returns `404` for session `missing`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status

### Requirement: CLI session mutating verbs surface session_active conflicts

When `mo issue session compact` or `mo issue session reset` receives an HTTP 409 response whose `code` is `session_active`, the CLI SHALL surface the server-provided `code` and `error`/`message` to the user (via `mohist-cli-format` output or stderr) rather than treating the response as a generic failure or silently succeeding. The CLI SHALL exit with a non-zero status. This is load-bearing: an agent that believes a compact/reset succeeded when it was rejected will keep operating on a polluted or full context.

#### Scenario: Compact on active session surfaces the conflict

- **WHEN** the user runs `mo issue session compact 42 plan`
- **AND** the server returns `409` with `code: "session_active"`
- **THEN** the CLI SHALL print the server-provided error message and the `session_active` code
- **AND** SHALL NOT report success
- **AND** SHALL exit with a non-zero status

#### Scenario: Reset on active session surfaces the conflict

- **WHEN** the user runs `mo issue session reset 42 plan`
- **AND** the server returns `409` with `code: "session_active"`
- **THEN** the CLI SHALL print the server-provided error message and the `session_active` code
- **AND** SHALL NOT report success
- **AND** SHALL exit with a non-zero status
