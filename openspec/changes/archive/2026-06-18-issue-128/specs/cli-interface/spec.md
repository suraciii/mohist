## ADDED Requirements

### Requirement: CLI provides mo agent command group

The CLI SHALL provide a top-level `mo agent` command group with `create`, `list`, `show`, `update`, and `delete` subcommands, mirroring the existing `mo issue` verb model. The command group SHALL communicate with the server through the shared `apiClient` and SHALL NOT contain business logic. All commands SHALL require the server to be running and SHALL surface the standard "Server is not running" error when it is unavailable.

#### Scenario: agent subcommands appear in help

- **WHEN** the user runs `mo agent --help`
- **THEN** the output SHALL list `create`, `list`, `show`, `update`, and `delete` subcommands

#### Scenario: agent commands require server

- **WHEN** the user runs any `mo agent` subcommand
- **AND** the server is not running
- **THEN** the CLI SHALL display "Server is not running. Start with: mo server start"
- **AND** SHALL exit with a non-zero status

### Requirement: mo agent create

`mo agent create` SHALL accept `--name <n>` (required) and `--instructions <text>` (required), and SHALL create an Agent in the current project context via `POST /agents`. It SHALL return the created agent id on success. It SHALL accept optional flags `--description <text>`, `--agent-config <json|@file>`, `--skills <csv>`, and `--max-concurrent-runs <int>`. The `--instructions` flag SHALL accept a literal string, a curl-style `@file` reference, and `-` to read from stdin, consistent with `mo issue create --body` behavior. When the server returns a name-conflict error, the CLI SHALL surface a readable conflict message and exit non-zero.

#### Scenario: Create with required fields

- **WHEN** the user runs `mo agent create --name reviewer --instructions "You are a senior reviewer."`
- **THEN** the CLI SHALL send `POST /agents` with `name` and `instructions`
- **AND** the CLI SHALL print the created agent id

#### Scenario: Create with optional fields

- **WHEN** the user runs `mo agent create --name reviewer --instructions "..." --description "..." --agent-config '{"model":"..."}' --skills "mohist,fsd" --max-concurrent-runs 2`
- **THEN** the CLI SHALL send all provided optional fields in the create request body

#### Scenario: Instructions read from file or stdin

- **WHEN** the user runs `mo agent create --name reviewer --instructions @prompt.md` or `--instructions -`
- **THEN** the CLI SHALL resolve the instructions text from the file or stdin before sending the request
- **AND** SHALL send the resolved text verbatim

#### Scenario: Missing required field fails

- **WHEN** the user runs `mo agent create` without `--name` or `--instructions`
- **THEN** the CLI SHALL print a clear validation error
- **AND** SHALL exit with code 1

#### Scenario: Name conflict surfaced clearly

- **WHEN** the user runs `mo agent create --name reviewer` and the server returns HTTP 409
- **THEN** the CLI SHALL print a readable conflict error naming the conflicting `name`
- **AND** SHALL exit with a non-zero status

### Requirement: mo agent list with status filters

`mo agent list` SHALL list Agents in the current project context. By default it SHALL list only `status` = `active` Agents. It SHALL support `--all` to include archived Agents and `--status <status>` to filter to a single status value (e.g. `--status archived`). The output SHALL be tabular and human-readable by default.

#### Scenario: List defaults to active

- **WHEN** the user runs `mo agent list`
- **THEN** the CLI SHALL call `GET /agents`
- **AND** SHALL display only active Agents

#### Scenario: List includes archived with --all

- **WHEN** the user runs `mo agent list --all`
- **THEN** the CLI SHALL call `GET /agents?all=true`
- **AND** SHALL display both active and archived Agents

#### Scenario: List filtered by status

- **WHEN** the user runs `mo agent list --status archived`
- **THEN** the CLI SHALL call `GET /agents?status=archived`
- **AND** SHALL display only archived Agents

### Requirement: mo agent show accepts name or id

`mo agent show <name-or-id>` SHALL resolve the argument as either the Agent `name` or `id` in the current project context and SHALL display the full Agent record, including `createdAt` and `updatedAt`.

#### Scenario: Show by name

- **WHEN** the user runs `mo agent show reviewer`
- **AND** an Agent named `reviewer` exists in the current project
- **THEN** the CLI SHALL resolve the name to the Agent and display the full record

#### Scenario: Show by id

- **WHEN** the user runs `mo agent show agent_abc123`
- **THEN** the CLI SHALL display the full Agent record for that id

#### Scenario: Show includes timestamps

- **WHEN** the user runs `mo agent show <name-or-id>` for any existing Agent
- **THEN** the output SHALL include `createdAt` and `updatedAt`

#### Scenario: Show unknown Agent fails

- **WHEN** the user runs `mo agent show <name-or-id>` and no matching Agent exists
- **THEN** the CLI SHALL print a clear not-found error
- **AND** SHALL exit with a non-zero status

### Requirement: mo agent update

`mo agent update <name-or-id>` SHALL resolve the argument as name or id and SHALL accept updates to `--name`, `--description`, `--instructions`, `--agent-config`, `--skills`, and `--max-concurrent-runs`. A rename SHALL be subject to the same project-scoped uniqueness rules as create. The CLI SHALL NOT permit changing `createdAt` and SHALL reflect the refreshed `updatedAt` returned by the server.

#### Scenario: Update mutable fields

- **WHEN** the user runs `mo agent update reviewer --instructions "New prompt"`
- **THEN** the CLI SHALL send `PATCH /agents/{id}` with the changed field
- **AND** SHALL display the updated Agent

#### Scenario: Rename applies uniqueness check

- **WHEN** the user runs `mo agent update reviewer --name coder`
- **AND** `coder` is already used by another Agent in the project
- **THEN** the CLI SHALL surface the server's 409 conflict as a readable error
- **AND** SHALL exit with a non-zero status

#### Scenario: Update reflects refreshed updatedAt

- **WHEN** the user runs `mo agent update <name-or-id>` and the update succeeds
- **THEN** the displayed record SHALL show a refreshed `updatedAt`

### Requirement: mo agent delete performs soft archive

`mo agent delete <name-or-id>` SHALL resolve the argument as name or id and SHALL call `DELETE /agents/{id}`, which archives the Agent. The CLI output SHALL make clear that the Agent was archived rather than hard-deleted.

#### Scenario: Delete archives the Agent

- **WHEN** the user runs `mo agent delete reviewer`
- **THEN** the CLI SHALL call `DELETE /agents/{id}`
- **AND** the output SHALL state that the Agent was archived

#### Scenario: Deleted name cannot be reused

- **WHEN** the user runs `mo agent delete reviewer`
- **AND** later runs `mo agent create --name reviewer`
- **THEN** the create SHALL fail with a name-conflict error
- **AND** the CLI SHALL surface the conflict clearly

#### Scenario: Delete unknown Agent fails

- **WHEN** the user runs `mo agent delete <name-or-id>` and no matching Agent exists
- **THEN** the CLI SHALL print a clear not-found error
- **AND** SHALL exit with a non-zero status
