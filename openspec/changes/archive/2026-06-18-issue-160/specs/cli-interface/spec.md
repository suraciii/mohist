## MODIFIED Requirements

### Requirement: Epic CLI Commands

CLI SHALL provide a `mo epic` top-level command group — peer to `mo issue` and `mo project` — that wires eight subcommands to the existing project-scoped Epic HTTP endpoints (`EpicRoutes.cs`). The CLI SHALL NOT modify Epic domain state directly; it SHALL only consume the existing HTTP API. All `mo epic` subcommands SHALL accept `--project <name>` / `--project-id <id>` to override the current active project (same resolution mechanism as `mo issue`), and `-o table|json` for output selection (table shape via `MohistCliApi.TableShape`). Epic and Issue share the project-local numbering namespace but are distinct entities; the `mo epic` group SHALL access Epic entities only and SHALL NOT silently fall through to Issue data.

#### Scenario: List Epics

- **WHEN** a user runs `mo epic list`
- **THEN** the CLI calls `GET /api/projects/{projectRef}/epics` for the resolved project
- **AND** in `table` mode prints Epic number, title, status, and priority for each Epic
- **AND** in `json` mode returns the complete API response fields unchanged
- **AND** an empty project prints a clear empty state rather than an error

#### Scenario: Create Epic

- **WHEN** a user runs `mo epic create <title> [--description <text>] [--priority <p0|p1|p2|p3>]`
- **THEN** the CLI sends `POST /api/projects/{projectRef}/epics` with title, optional description, and optional priority
- **AND** prints the newly created Epic identifier on success

#### Scenario: Create Epic missing title fails clearly

- **WHEN** a user runs `mo epic create` without a title argument
- **THEN** the CLI prints a clear validation error
- **AND** exits with a non-zero status without calling the API

#### Scenario: Show Epic by number or id

- **WHEN** a user runs `mo epic show <id|num>`
- **THEN** the CLI calls `GET /api/projects/{projectRef}/epics/{id}` passing the argument verbatim to the API's dual-track resolver
- **AND** prints Epic description, status, priority, projected progress, next issue, and the linked issue list

#### Scenario: Epic show is namespace-isolated from issue show

- **WHEN** a user runs `mo epic show 8`
- **THEN** the CLI returns Epic #8 (for example, the Labels Epic)
- **AND** it SHALL NOT return Issue #8 (a workflow task) even though both share the project-local number 8

#### Scenario: Update Epic fields

- **WHEN** a user runs `mo epic update <id|num> [--title <text>] [--description <text>] [--priority <p0|p1|p2|p3>]`
- **THEN** the CLI sends `PATCH /api/projects/{projectRef}/epics/{id}` with only the supplied optional fields
- **AND** prints the updated Epic on success

#### Scenario: Link an issue into an Epic

- **WHEN** a user runs `mo epic link <epic-id|num> <issue-id|num>`
- **THEN** the CLI sends `POST /api/projects/{projectRef}/epics/{id}/issues` with the issue reference
- **AND** prints a clear success confirmation identifying both the Epic and the linked issue

#### Scenario: Link surfaces duplicate membership conflict

- **WHEN** a user runs `mo epic link` for an issue that already belongs to another Epic
- **AND** the API returns a `DUPLICATE_EPIC_MEMBERSHIP` conflict
- **THEN** the CLI surfaces the conflict clearly, identifying the existing Epic
- **AND** the CLI SHALL NOT report silent success
- **AND** the CLI exits with a non-zero status

#### Scenario: Unlink an issue from an Epic

- **WHEN** a user runs `mo epic unlink <epic-id|num> <issue-id>`
- **THEN** the CLI sends `DELETE /api/projects/{projectRef}/epics/{id}/issues/{issueId}`
- **AND** prints a clear success confirmation

#### Scenario: Mark Epic done when all issues delivered

- **WHEN** a user runs `mo epic done <id|num>`
- **AND** every linked issue is delivered
- **THEN** the CLI sends `POST /api/projects/{projectRef}/epics/{id}/done`
- **AND** prints confirmation that the Epic status changed to `done`

#### Scenario: Done surfaces not-ready conflict

- **WHEN** a user runs `mo epic done <id|num>`
- **AND** the Epic still has undelivered linked issues
- **AND** the API returns an `EPIC_NOT_READY_TO_MARK_DONE` conflict
- **THEN** the CLI surfaces the conflict clearly, indicating undelivered issues block the transition
- **AND** the CLI SHALL NOT report silent success
- **AND** the CLI exits with a non-zero status

#### Scenario: Close Epic

- **WHEN** a user runs `mo epic close <id|num>`
- **THEN** the CLI sends `POST /api/projects/{projectRef}/epics/{id}/close`
- **AND** prints confirmation that the Epic status changed to `closed`

#### Scenario: Terminal Epic lifecycle transition surfaces already-terminal conflict

- **WHEN** a user runs `mo epic done` or `mo epic close` on an Epic that is already terminal
- **AND** the API returns an `EPIC_ALREADY_TERMINAL` conflict
- **THEN** the CLI surfaces the conflict clearly
- **AND** the CLI SHALL NOT report silent success
- **AND** the CLI exits with a non-zero status

#### Scenario: Project override applies to all subcommands

- **WHEN** a user runs any `mo epic` subcommand with `--project <name>` or `--project-id <id>`
- **THEN** the CLI resolves the target project via the same mechanism as `mo issue`
- **AND** all Epic API calls for that invocation use the resolved project

#### Scenario: Output format selection applies to all subcommands

- **WHEN** a user runs any `mo epic` subcommand with `-o table` or `-o json`
- **THEN** the CLI formats output accordingly
- **AND** table mode uses `MohistCliApi.TableShape`
- **AND** json mode emits the API response verbatim without table formatting, color codes, or borders

#### Scenario: Epic group help

- **WHEN** a user runs `mo epic --help`
- **THEN** the CLI lists all eight subcommands: `list`, `create`, `show`, `update`, `link`, `unlink`, `done`, `close`
- **AND** each subcommand `--help` lists its positional arguments and options

#### Scenario: No Epic start command

- **WHEN** a user inspects `mo epic` subcommands
- **THEN** no subcommand starts workflow execution for an Epic
- **AND** Epics remain non-executable goal containers (status transitions only)

#### Scenario: CLI integration test coverage

- **WHEN** the CLI integration test suite runs
- **THEN** it SHALL cover `mo epic list` for both empty and non-empty projects
- **AND** it SHALL cover `mo epic create` failing clearly when the title argument is missing
- **AND** it SHALL cover `mo epic link` surfacing the duplicate-membership conflict
- **AND** it SHALL cover `mo epic done` surfacing the not-ready conflict
