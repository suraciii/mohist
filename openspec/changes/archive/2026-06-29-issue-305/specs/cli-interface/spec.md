## MODIFIED Requirements

### Requirement: CLI provides mo issue session command group

The CLI SHALL provide `mo issue session` (singular) as a command group under `mo issue`, exposing five subcommands: `show`, `transcript`, `compact`, `reset`, and `followup`. The group SHALL be distinct from the existing `mo issue sessions` (plural, list) command, which SHALL remain unchanged and is the source of the `<name>` positional argument used by the verbs. Each subcommand SHALL accept the issue number and session name positional arguments, the `--project` / `--project-id` project-reference options, and the `-o table|json` output option. `mo issue session --help` SHALL list the five subcommands and SHALL document that `<name>` comes from the `mo issue sessions <num>` listing.

#### Scenario: Help lists the five session subcommands

- **WHEN** the user runs `mo issue session --help`
- **THEN** the output SHALL list the subcommands `show`, `transcript`, `compact`, `reset`, and `followup`

#### Scenario: All session subcommands accept project reference and output options

- **WHEN** the user runs any `mo issue session <subcommand> <num> <name>` with `--project <name>` (or `--project-id <id>`) and `-o json`
- **THEN** the CLI SHALL resolve the project and emit output in JSON

#### Scenario: Existing list command is preserved

- **WHEN** the user runs `mo issue sessions <num>`
- **THEN** the CLI SHALL behave identically to before this change
- **AND** SHALL list coder sessions for the issue

## ADDED Requirements

### Requirement: CLI issue session followup pushes text into a running session

The CLI SHALL provide `mo issue session followup <num> <name>` to send a followup instruction into a running issue workflow session by issuing `POST /api/projects/:projectId/issues/:number/sessions/:name/followup` with the text body. The command SHALL accept a required followup text source chosen from exactly one of `--text <text>`, `--text-file <path>` (read as UTF-8), or `--text-stdin` (read from standard input), mirroring `mo agent session followup`. On a `200` response the CLI SHALL print the server-provided delivery status. The command SHALL accept `--project`/`--project-id` and `-o table|json`.

#### Scenario: Followup prints the delivery status

- **WHEN** the user runs `mo issue session followup 42 plan --text "add a logout route"`
- **THEN** the CLI sends `POST /api/projects/:projectId/issues/42/sessions/plan/followup` with the text
- **AND** prints the delivery status returned by the server

#### Scenario: Followup reads the text from a file

- **WHEN** the user runs `mo issue session followup 42 plan --text-file note.md`
- **THEN** the CLI reads `note.md` as UTF-8 text
- **AND** sends the file contents as the followup text

#### Scenario: Followup reads the text from stdin

- **WHEN** the user runs `echo "continue" | mo issue session followup 42 plan --text-stdin`
- **THEN** the CLI reads the followup text from standard input
- **AND** sends the stdin contents as the followup text

#### Scenario: Followup in JSON emits the raw payload

- **WHEN** the user runs `mo issue session followup 42 plan --text "Hi" -o json`
- **THEN** the CLI SHALL print the server response as JSON

#### Scenario: Inactive session surfaces the conflict honestly

- **WHEN** the user runs `mo issue session followup 42 plan --text "Hi"`
- **AND** the server returns `409` with `code: "session_inactive"` because the session is no longer active
- **THEN** the CLI SHALL print the server-provided error message and the `session_inactive` code
- **AND** SHALL NOT report success
- **AND** SHALL exit with a non-zero status

#### Scenario: Runner offline surfaces the state honestly

- **WHEN** the user runs `mo issue session followup 42 plan --text "Hi"`
- **AND** the server returns `503` with `code: "runner_offline"`
- **THEN** the CLI SHALL print the server-provided error message and the `runner_offline` code
- **AND** SHALL NOT report success
- **AND** SHALL exit with a non-zero status

#### Scenario: Unknown session surfaces server error

- **WHEN** the user runs `mo issue session followup 42 missing --text "Hi"`
- **AND** the server returns `404` for session `missing`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status

#### Scenario: Missing text fails clearly

- **WHEN** the user runs `mo issue session followup 42 plan` without `--text`, `--text-file`, or `--text-stdin`
- **THEN** the CLI prints a clear validation error
- **AND** exits with code 1

### Requirement: CLI provides mo project workflow command group

The CLI SHALL provide `mo project workflow` as a command group under the existing `mo project` command, exposing two subgroups: `template` (with `list`, `create`, `show`, `update`, and `delete` verbs) and `config` (with `get`, `set`, `clear`, and `preview` verbs). Every subcommand in the group SHALL resolve the target project via `--project`/`--project-id` (or the active-project fallback) and SHALL accept `-o table|json`. `mo project workflow --help` SHALL list the `template` and `config` subgroups. The project-level group SHALL be distinct from the per-issue `mo issue workflow config` group and SHALL NOT alter it.

#### Scenario: Help lists the template and config subgroups

- **WHEN** the user runs `mo project workflow --help`
- **THEN** the output SHALL list the `template` and `config` subgroups

#### Scenario: Template help lists the five verbs

- **WHEN** the user runs `mo project workflow template --help`
- **THEN** the output SHALL list the verbs `list`, `create`, `show`, `update`, and `delete`

#### Scenario: Config help lists the four verbs

- **WHEN** the user runs `mo project workflow config --help`
- **THEN** the output SHALL list the verbs `get`, `set`, `clear`, and `preview`

#### Scenario: All subcommands accept project reference and output options

- **WHEN** the user runs any `mo project workflow <subgroup> <verb>` with `--project <name>` (or `--project-id <id>`) and `-o json`
- **THEN** the CLI SHALL resolve the project and emit output in JSON

### Requirement: CLI project workflow template list shows project templates

`mo project workflow template list` SHALL send `GET /api/projects/:projectId/workflow-templates` and render the returned template list. The output SHALL support `-o table` (human-readable, one template per row) and `-o json` (raw server payload).

#### Scenario: List renders the template list

- **WHEN** the user runs `mo project workflow template list -o table`
- **THEN** the CLI sends `GET /api/projects/:projectId/workflow-templates`
- **AND** the rendered output SHALL present the project's workflow templates

#### Scenario: List in JSON emits the raw payload

- **WHEN** the user runs `mo project workflow template list -o json`
- **THEN** the CLI SHALL print the server response as JSON

### Requirement: CLI project workflow template create accepts inline or file YAML body

`mo project workflow template create` SHALL send `POST /api/projects/:projectId/workflow-templates` with a YAML body. The command SHALL accept the template body as an inline string positional/flag value or as a curl-style `@file` reference that reads the file as UTF-8 YAML. On a `201` response the CLI SHALL print the created template identifier. When the server rejects the body as invalid YAML, the CLI SHALL print the server-provided error message and exit with a non-zero status.

#### Scenario: Create from inline YAML

- **WHEN** the user runs `mo project workflow template create --yaml "<inline yaml>"`
- **THEN** the CLI sends `POST /api/projects/:projectId/workflow-templates` with the inline YAML as the `yaml` body
- **AND** prints the created template identifier on success

#### Scenario: Create from file

- **WHEN** the user runs `mo project workflow template create --yaml @wf.yaml`
- **THEN** the CLI reads `wf.yaml` as UTF-8
- **AND** sends the file contents as the `yaml` body

#### Scenario: Invalid YAML surfaces server error

- **WHEN** the user runs `mo project workflow template create --yaml "@bad.yaml"` and the server rejects the template as invalid
- **THEN** the CLI SHALL print the server error message
- **AND** SHALL exit with a non-zero status

### Requirement: CLI project workflow template show renders a template definition

`mo project workflow template show <templateId>` SHALL send `GET /api/projects/:projectId/workflow-templates/:templateId` and render the returned template definition. The output SHALL support `-o table` and `-o json`. When the template does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Show renders the template definition

- **WHEN** the user runs `mo project workflow template show tpl_abc -o table`
- **THEN** the CLI sends `GET /api/projects/:projectId/workflow-templates/tpl_abc`
- **AND** the rendered output SHALL present the template definition

#### Scenario: Show nonexistent template surfaces error

- **WHEN** the user runs `mo project workflow template show nope`
- **AND** the server returns `404` for template `nope`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status

### Requirement: CLI project workflow template update accepts inline or file YAML body

`mo project workflow template update <templateId>` SHALL send `PUT /api/projects/:projectId/workflow-templates/:templateId` with a YAML body. The command SHALL accept the template body as an inline string or as a curl-style `@file` reference that reads the file as UTF-8 YAML, consistent with `create`. On success the CLI SHALL print the updated template. When the template does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Update from inline YAML

- **WHEN** the user runs `mo project workflow template update tpl_abc --yaml "<inline yaml>"`
- **THEN** the CLI sends `PUT /api/projects/:projectId/workflow-templates/tpl_abc` with the inline YAML as the body

#### Scenario: Update from file

- **WHEN** the user runs `mo project workflow template update tpl_abc --yaml @wf.yaml`
- **THEN** the CLI reads `wf.yaml` as UTF-8
- **AND** sends the file contents as the `yaml` body

#### Scenario: Update nonexistent template surfaces error

- **WHEN** the user runs `mo project workflow template update nope --yaml "<yaml>"`
- **AND** the server returns `404` for template `nope`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status

### Requirement: CLI project workflow template delete removes a template

`mo project workflow template delete <templateId>` SHALL send `DELETE /api/projects/:projectId/workflow-templates/:templateId`. On success the CLI SHALL print confirmation that the template was deleted. When the template does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Delete removes the template

- **WHEN** the user runs `mo project workflow template delete tpl_abc`
- **THEN** the CLI sends `DELETE /api/projects/:projectId/workflow-templates/tpl_abc`
- **AND** prints confirmation that the template was deleted

#### Scenario: Delete nonexistent template surfaces error

- **WHEN** the user runs `mo project workflow template delete nope`
- **AND** the server returns `404` for template `nope`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status

### Requirement: CLI project workflow config get reads the project workflow profile

`mo project workflow config get` SHALL read the project's workflow profile and render it so the user can see the complete project-level override state in one view: the default-template id, the variables, and the prompt overrides. The command SHALL support `-o table` (human-readable) and `-o json` (raw payload).

#### Scenario: Get renders default template, variables, and prompts

- **WHEN** the user runs `mo project workflow config get -o table`
- **THEN** the CLI SHALL read the project workflow profile
- **AND** the rendered output SHALL surface the default-template id, the variables, and the prompt overrides

#### Scenario: Get in JSON emits the raw payload

- **WHEN** the user runs `mo project workflow config get -o json`
- **THEN** the CLI SHALL print the raw server payload as JSON

### Requirement: CLI project workflow config set composes default-template, variable, and prompt changes

`mo project workflow config set` SHALL be a composite command: only the categories whose flags are present on the invocation are changed, and categories whose flags are absent SHALL NOT be touched. It SHALL accept these repeatable, independent flags:
- `--default-template <id>` — sends `PUT /api/projects/:projectId/workflow-profile/default-template`, selecting which existing template is the project default. This flag selects a template id and is distinct from the per-issue `--template` body flag; project template bodies are managed via `mo project workflow template create|update`.
- `--var k=v` — contributes an entry to an incremental variables merge sent as `PATCH /api/projects/:projectId/workflow-profile/variables`.
- `--stage-var <stage>.k=v` — contributes a stage-scoped entry (`<stage>` mapping to `k`) to the same incremental variables PATCH.
- `--vars-file <file>` — reads a JSON variable bundle from the file and sends a wholesale full-replace as `PUT /api/projects/:projectId/workflow-profile/variables`. This full-replace form is unique to the project API (the per-issue API only supports incremental merge) and SHALL be mutually exclusive with `--var`/`--stage-var`.
- `--prompt <key>=<body|@file>` — for each occurrence sends `PUT /api/projects/:projectId/workflow-profile/prompts/<key>`. When the body begins with `@`, the CLI SHALL read the prompt body from the referenced file as UTF-8; otherwise the body is the inline prompt text.

When no flag is present, the command SHALL make no mutating request and exit with a non-zero status and a clear message. When only `--var` / `--stage-var` are present, the default-template and prompts SHALL remain unchanged.

#### Scenario: Set default template

- **WHEN** the user runs `mo project workflow config set --default-template tpl_abc`
- **THEN** the CLI sends `PUT /api/projects/:projectId/workflow-profile/default-template` with template id `tpl_abc`
- **AND** a subsequent `mo project workflow config get` SHALL reflect the new default-template id

#### Scenario: Incremental variable merge via flags

- **WHEN** the user runs `mo project workflow config set --var foo=bar --stage-var plan.baz=qux`
- **THEN** the CLI sends a variables PATCH to `/api/projects/:projectId/workflow-profile/variables` containing `foo=bar` and a stage-scoped `plan.baz=qux`
- **AND** the CLI SHALL NOT send any default-template or prompts request
- **AND** variables not mentioned SHALL be preserved

#### Scenario: Full variable replace from file

- **WHEN** the user runs `mo project workflow config set --vars-file vars.json`
- **AND** `vars.json` contains a JSON variable bundle
- **THEN** the CLI reads `vars.json` as UTF-8 JSON
- **AND** sends a wholesale `PUT /api/projects/:projectId/workflow-profile/variables` that replaces the entire variable set
- **AND** any previously-existing variable not present in the file SHALL be removed

#### Scenario: Full replace and incremental merge are mutually exclusive

- **WHEN** the user runs `mo project workflow config set --vars-file vars.json --var foo=bar`
- **THEN** the CLI SHALL print a clear validation error explaining the flags are mutually exclusive
- **AND** SHALL exit with a non-zero status

#### Scenario: Set prompt from inline text and from file

- **WHEN** the user runs `mo project workflow config set --prompt greeting="You are..."`
- **THEN** the CLI sends `PUT /api/projects/:projectId/workflow-profile/prompts/greeting` with the inline body
- **WHEN** the user runs `mo project workflow config set --prompt greeting=@prompts/greeting.md`
- **THEN** the CLI reads `prompts/greeting.md` as UTF-8
- **AND** sends `PUT /api/projects/:projectId/workflow-profile/prompts/greeting` with the file contents as the body

#### Scenario: Composite invocation touches multiple categories

- **WHEN** the user runs `mo project workflow config set --default-template tpl_abc --var foo=bar --prompt greeting="Hi"`
- **THEN** the CLI SHALL issue the default-template PUT, the variables PATCH, and the prompt PUT
- **AND** each request SHALL be independent

#### Scenario: No flags makes no change

- **WHEN** the user runs `mo project workflow config set` with no flags
- **THEN** the CLI SHALL make no mutating request
- **AND** SHALL exit with a non-zero status and a message stating nothing to change

### Requirement: CLI project workflow config clear composes default-template, variable, and prompt removals

`mo project workflow config clear` SHALL be a composite command that removes only the categories whose flags are present:
- `--default-template` — sends `DELETE /api/projects/:projectId/workflow-profile/default-template`, clearing the project default so issues fall back to the system template.
- `--var k` — contributes a removal of variable `k` via an incremental variables PATCH that sets `k` to `null`.
- `--prompt <key>` — sends `DELETE /api/projects/:projectId/workflow-profile/prompts/<key>`.

Categories whose flags are absent SHALL NOT be affected. When no flag is present, the command SHALL make no mutating request and exit non-zero with a clear message.

#### Scenario: Clear default template falls back to system default

- **WHEN** the user runs `mo project workflow config clear --default-template`
- **THEN** the CLI sends `DELETE /api/projects/:projectId/workflow-profile/default-template`
- **AND** the project's default-template id SHALL resolve to unset

#### Scenario: Clear specific variables and prompts without affecting others

- **WHEN** the user runs `mo project workflow config clear --var foo --prompt greeting`
- **THEN** the CLI sends a variables PATCH setting `foo` to `null`
- **AND** the CLI sends `DELETE /api/projects/:projectId/workflow-profile/prompts/greeting`
- **AND** other variables and prompts SHALL remain unchanged

#### Scenario: No flags makes no change

- **WHEN** the user runs `mo project workflow config clear` with no flags
- **THEN** the CLI SHALL make no mutating request
- **AND** SHALL exit with a non-zero status and a message stating nothing to clear

### Requirement: CLI project workflow config preview renders a prompt

`mo project workflow config preview <key>` SHALL send `POST /api/projects/:projectId/workflow-profile/prompts/<key>/preview` and print the rendered prompt text. The preview SHALL reflect the project's current variables and template so the user can diagnose the final prompt an agent would receive.

#### Scenario: Preview renders the final prompt

- **WHEN** the user runs `mo project workflow config preview plan_prompt`
- **THEN** the CLI sends `POST /api/projects/:projectId/workflow-profile/prompts/plan_prompt/preview`
- **AND** SHALL print the rendered prompt text

### Requirement: CLI issue archive batch-archives all completed issues

`mo issue archive --all-completed` SHALL send `POST /api/projects/:projectId/issues/archive-completed` to archive every completed, not-yet-archived issue in the resolved project in a single request, and SHALL print the server-provided result. The `--all-completed` flag SHALL be distinct from the single-issue `mo issue archive <number>` form. The command SHALL accept `--project`/`--project-id`. When no project can be resolved, the CLI SHALL print a clear error and exit with a non-zero status.

#### Scenario: Batch archive archives all completed issues

- **WHEN** the user runs `mo issue archive --all-completed`
- **THEN** the CLI sends `POST /api/projects/:projectId/issues/archive-completed`
- **AND** prints the server-provided result

#### Scenario: Batch archive in JSON emits the raw payload

- **WHEN** the user runs `mo issue archive --all-completed -o json`
- **THEN** the CLI SHALL print the server response as JSON

#### Scenario: Batch archive requires a resolved project

- **WHEN** the user runs `mo issue archive --all-completed` with no resolvable project
- **THEN** the CLI SHALL print a clear error explaining no project is resolved
- **AND** SHALL exit with a non-zero status

#### Scenario: Single-issue archive and batch flag are mutually exclusive

- **WHEN** the user runs `mo issue archive 42 --all-completed`
- **THEN** the CLI SHALL print a clear validation error explaining `<number>` and `--all-completed` are mutually exclusive
- **AND** SHALL exit with a non-zero status
