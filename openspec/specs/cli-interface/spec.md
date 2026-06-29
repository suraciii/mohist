### Requirement: CLI issue update omits unchanged optional fields from PATCH body

`mo issue update <number>` SHALL include a field in the PATCH request body only when the user explicitly provides the corresponding flag. When `--label/-l` is not passed, the CLI SHALL NOT include a `labels` key in the request body. When `--title`, `--body`, `--priority`, or the draft-state option is not passed, the CLI SHALL NOT include the corresponding key. This ensures the server's raw-presence-aware merge semantics preserve all unmentioned fields.

#### Scenario: Update body without touching labels

- **WHEN** the user runs `mo issue update 42 --body-file body.md`
- **AND** no `--label` flag is provided
- **THEN** the CLI SHALL NOT include a `labels` key in the PATCH request body
- **AND** the issue's existing labels SHALL remain unchanged after the update

#### Scenario: Update labels without touching title or body

- **WHEN** the user runs `mo issue update 42 --label stream=backend`
- **THEN** the PATCH request body SHALL contain `labels`
- **AND** the PATCH request body SHALL NOT contain `title`, `body`, or `priority` keys

#### Scenario: Omit all optional flags sends empty PATCH

- **WHEN** the user runs `mo issue update 42` with no optional flags
- **THEN** the CLI SHALL send a PATCH request with no optional field keys in the body
- **AND** the issue SHALL remain unchanged

### Requirement: CLI issue create accepts execution configuration flags

`mo issue create <title>` SHALL accept `--repository <name>`, `--stage-models <json|@file>`, and `--stage-model-variants <json|@file>` flags. These flags SHALL be sent in the `POST /api/issues` request body alongside title, body, labels, priority, and model when provided. The `--repository` flag SHALL select the target repository in multi-repository projects. The `--stage-models` and `--stage-model-variants` flags SHALL accept an inline JSON string or a curl-style `@file` reference that reads JSON from a file.

#### Scenario: Create issue with repository

- **WHEN** the user runs `mo issue create "Fix bug" --repository feature-repo`
- **THEN** the CLI sends `repository: "feature-repo"` in the create request body
- **AND** the issue is created in the specified repository

#### Scenario: Create issue with stage models

- **WHEN** the user runs `mo issue create "Fix bug" --stage-models '{"plan":"anthropic/claude-sonnet"}'`
- **THEN** the CLI sends `stageModels: { "plan": "anthropic/claude-sonnet" }` in the create request body
- **AND** `mo issue show <number>` reflects the stage model configuration

#### Scenario: Create issue with stage models from file

- **WHEN** the user runs `mo issue create "Fix bug" --stage-models @models.json`
- **AND** `models.json` contains `{ "plan": "anthropic/claude-sonnet", "check": "openai/gpt-5" }`
- **THEN** the CLI reads `models.json` as UTF-8 JSON
- **AND** sends the parsed object as `stageModels` in the create request body

#### Scenario: Create issue with stage model variants from file

- **WHEN** the user runs `mo issue create "Fix bug" --stage-model-variants @variants.json`
- **AND** `variants.json` contains `{ "plan": "max", "check": "high" }`
- **THEN** the CLI reads `variants.json` as UTF-8 JSON
- **AND** sends the parsed object as `stageModelVariants` in the create request body

#### Scenario: Invalid stage models JSON fails clearly

- **WHEN** the user runs `mo issue create "Fix bug" --stage-models 'not-json'`
- **THEN** the CLI prints a clear validation error
- **AND** exits with code 1

### Requirement: CLI issue update accepts stage model configuration

`mo issue update <number>` SHALL accept `--stage-models <json|@file>` and `--stage-model-variants <json|@file>` flags. These flags SHALL be sent in the PATCH request body as `stageModels` and `stageModelVariants` respectively. The CLI SHALL NOT accept `--repository` on update because repository ownership is immutable after issue creation. The `@file` reference SHALL read JSON from a file, consistent with `--body @file` behavior.

#### Scenario: Update stage models

- **WHEN** the user runs `mo issue update 42 --stage-models '{"plan":"openai/gpt-5"}'`
- **THEN** the CLI sends `stageModels: { "plan": "openai/gpt-5" }` in the PATCH request body
- **AND** the updated stage models are persisted and visible via `mo issue show 42`

#### Scenario: Update stage model variants from file

- **WHEN** the user runs `mo issue update 42 --stage-model-variants @variants.json`
- **THEN** the CLI reads `variants.json` as UTF-8 JSON
- **AND** sends the parsed object as `stageModelVariants` in the PATCH request body

#### Scenario: Repository flag rejected on update

- **WHEN** the user runs `mo issue update 42 --repository other-repo`
- **THEN** the CLI prints a clear error explaining repository is immutable after creation
- **AND** exits with a non-zero status

### Requirement: CLI provides mo issue prereq subcommands

The CLI SHALL provide `mo issue prereq` as a command group with `add` and `remove` subcommands that manage issue-level start prerequisites via the existing server API. `mo issue prereq add <number> <prereq-number>` SHALL send `POST /api/issues/:number/prerequisites` with the prerequisite issue number. `mo issue prereq remove <number> <prereq-number>` SHALL send `DELETE /api/issues/:number/prerequisites/:prereqNumber`. Both subcommands SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: Add a start prerequisite

- **WHEN** the user runs `mo issue prereq add 201 200`
- **THEN** the CLI sends `POST /api/issues/201/prerequisites` with prerequisite issue 200
- **AND** `mo issue show 201` lists issue 200 in `prerequisites`

#### Scenario: Remove a start prerequisite

- **WHEN** the user runs `mo issue prereq remove 201 200`
- **THEN** the CLI sends `DELETE /api/issues/201/prerequisites/200`
- **AND** `mo issue show 201` no longer lists issue 200 in `prerequisites`

#### Scenario: Circular prerequisite surfaces server error

- **WHEN** the user runs `mo issue prereq add 200 201`
- **AND** issue 201 already requires issue 200
- **AND** the server returns a `circular-prerequisite` rejection
- **THEN** the CLI prints the server-provided error message
- **AND** exits with a non-zero status

#### Scenario: Nonexistent prerequisite issue surfaces server error

- **WHEN** the user runs `mo issue prereq add 42 9999`
- **AND** issue 9999 does not exist
- **THEN** the CLI prints the server-provided error message
- **AND** does not report silent success
- **AND** exits with a non-zero status

#### Scenario: Prereq help lists subcommands

- **WHEN** the user runs `mo issue prereq --help`
- **THEN** the output lists `add` and `remove` subcommands

### Requirement: CLI provides mo issue comment add subcommand

The CLI SHALL provide `mo issue comment add <number> --body <text>` to create a comment via `POST /api/issues/:number/comments`. The command SHALL accept `--body` as a literal string and `--body-file` as a file reference for long comments, consistent with `mo issue create --body/--body-file` behavior. The command SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: Add a comment with inline body

- **WHEN** the user runs `mo issue comment add 42 --body "Looks good"`
- **THEN** the CLI sends `POST /api/issues/42/comments` with the body text
- **AND** prints the new comment identifier on success

#### Scenario: Add a comment from file

- **WHEN** the user runs `mo issue comment add 42 --body-file comment.md`
- **THEN** the CLI reads `comment.md` as UTF-8 text
- **AND** sends the file contents as the comment body

#### Scenario: Missing body fails clearly

- **WHEN** the user runs `mo issue comment add 42` without `--body` or `--body-file`
- **THEN** the CLI prints a clear validation error
- **AND** exits with code 1

#### Scenario: Comment help lists subcommands

- **WHEN** the user runs `mo issue comment --help`
- **THEN** the output lists `add` as a subcommand

### Requirement: CLI provides mo issue reject command

The CLI SHALL provide `mo issue reject <number> --message <text>` to request changes at an approval gate via `POST /api/issues/:number/reject`. The command SHALL accept `--message` as a required flag and SHALL accept `--project/--project-id` and `-o table|json`. The `--message` flag SHALL accept a literal string.

#### Scenario: Reject with message

- **WHEN** the user runs `mo issue reject 42 --message "Rework the auth flow"`
- **THEN** the CLI sends `POST /api/issues/42/reject` with the message
- **AND** prints confirmation that the rejection was submitted

#### Scenario: Missing message fails clearly

- **WHEN** the user runs `mo issue reject 42` without `--message`
- **THEN** the CLI prints a clear validation error
- **AND** exits with code 1

### Requirement: CLI provides mo issue stop command

The CLI SHALL provide `mo issue stop <number>` to perform a terminal stop via `POST /api/issues/:number/stop`. Terminal stop SHALL be distinct from `force-stop` (which pauses and can be resumed). The `--help` output SHALL explain that `stop` is terminal and cannot be resumed, distinguishing it from `force-stop` pause semantics. The command SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: Terminal stop

- **WHEN** the user runs `mo issue stop 42`
- **THEN** the CLI sends `POST /api/issues/42/stop`
- **AND** prints confirmation that the issue was stopped

#### Scenario: Stop help distinguishes from force-stop

- **WHEN** the user runs `mo issue stop --help`
- **THEN** the output explains that `stop` is a terminal action that cannot be resumed
- **AND** the output distinguishes `stop` from `force-stop` which pauses execution

### Requirement: CLI issue create accepts workflow profile flag

`mo issue create <title>` SHALL accept a `--workflow-profile <id>` flag that selects the issue's workflow profile selection. When provided, the CLI SHALL send `workflowProfileId` in the `POST /api/issues` request body. When omitted, the CLI SHALL NOT include a `workflowProfileId` key so the server applies default inheritance. `mo issue show <number>` SHALL display the issue's effective workflow profile.

#### Scenario: Create issue with workflow profile

- **WHEN** the user runs `mo issue create "Fix bug" --workflow-profile mohist/pr`
- **THEN** the CLI sends `workflowProfileId: "mohist/pr"` in the create request body
- **AND** `mo issue show <number>` displays workflow profile `mohist/pr`

#### Scenario: Create issue without workflow profile omits the key

- **WHEN** the user runs `mo issue create "Fix bug"` without `--workflow-profile`
- **THEN** the CLI SHALL NOT include a `workflowProfileId` key in the create request body
- **AND** the created issue resolves its profile via default inheritance

#### Scenario: Show displays the effective workflow profile

- **WHEN** the user runs `mo issue show 42`
- **THEN** the output SHALL display the issue's effective workflow profile id

### Requirement: CLI issue update accepts workflow profile flag

`mo issue update <number>` SHALL accept a `--workflow-profile <id>` flag that changes the issue's workflow profile selection by sending `workflowProfileId` in the `PATCH /api/issues/:number` request body. When the flag is omitted, the CLI SHALL NOT include a `workflowProfileId` key, preserving the issue's existing selection. When the server rejects the change because the issue has started, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Update workflow profile on backlog issue

- **WHEN** the user runs `mo issue update 42 --workflow-profile mohist/pr`
- **THEN** the CLI sends `workflowProfileId: "mohist/pr"` in the PATCH request body
- **AND** `mo issue show 42` displays workflow profile `mohist/pr`

#### Scenario: Omitting workflow profile flag preserves selection

- **WHEN** the user runs `mo issue update 42 --body "new body"` without `--workflow-profile`
- **THEN** the CLI SHALL NOT include a `workflowProfileId` key in the PATCH request body
- **AND** the issue's existing workflow profile selection SHALL remain unchanged

#### Scenario: Started issue surfaces rejection

- **WHEN** the user runs `mo issue update 42 --workflow-profile mohist/pr`
- **AND** the server rejects the change because issue 42 has an active workflow run
- **THEN** the CLI prints the server-provided error message
- **AND** exits with a non-zero status

### Requirement: CLI provides mo issue workflow config command group

The CLI SHALL provide `mo issue workflow config` as a command group under the existing `mo issue workflow` parent command. The group SHALL expose exactly four subcommands: `get`, `set`, `clear`, and `preview`. `mo issue workflow config --help` SHALL list all four subcommands. Every subcommand SHALL accept the issue number positional argument, the `--project` / `--project-id` project-reference options, and the `-o table|json` output option. The group SHALL be distinct from the runtime subcommands `mo issue workflow status` / `timeline` and SHALL NOT alter them.

#### Scenario: Help lists the four config subcommands

- **WHEN** the user runs `mo issue workflow config --help`
- **THEN** the output SHALL list the subcommands `get`, `set`, `clear`, and `preview`

#### Scenario: All subcommands accept project reference and output options

- **WHEN** the user runs any `mo issue workflow config <subcommand>` with `--project <name>` (or `--project-id <id>`) and `-o json`
- **THEN** the CLI SHALL resolve the project and emit output in JSON

### Requirement: CLI issue workflow config get reads the workflow profile

`mo issue workflow config get <num>` SHALL send `GET /api/projects/:projectId/issues/:number/workflow-profile` and render the returned workflow profile. The rendered output SHALL surface the three configuration sections — template, variables, and prompts — so the user can see the complete per-issue override state in one command. The output SHALL support `-o table` (human-readable) and `-o json` (raw payload).

#### Scenario: Get renders all three sections

- **WHEN** the user runs `mo issue workflow config get 42 -o table`
- **THEN** the CLI sends `GET /api/projects/:projectId/issues/42/workflow-profile`
- **AND** the rendered output SHALL present the template, variables, and prompts sections

#### Scenario: Get in JSON emits the raw payload

- **WHEN** the user runs `mo issue workflow config get 42 -o json`
- **THEN** the CLI SHALL print the server response as JSON

### Requirement: CLI issue workflow config set composes template, variable, and prompt changes

`mo issue workflow config set <num>` SHALL be a composite command: only the categories whose flags are present on the invocation are changed, and categories whose flags are absent SHALL NOT be touched. It SHALL accept these repeatable, independent flags:
- `--template <yaml|@file>` — sends `PUT /api/projects/:projectId/issues/:number/workflow-profile/template`. When the value begins with `@`, the CLI SHALL read the template body from the referenced file as UTF-8; otherwise the value is the inline template body.
- `--var k=v` — contributes an entry to a variables update sent as `PATCH /api/projects/:projectId/issues/:number/workflow-profile/variables`.
- `--stage-var <stage>.k=v` — contributes a stage-scoped entry (`<stage>` mapping to `k`) to the same variables PATCH.
- `--prompt <key>=<body|@file>` — for each occurrence sends `PUT /api/projects/:projectId/issues/:number/workflow-profile/prompts/<key>`. When the body begins with `@`, the CLI SHALL read the prompt body from the referenced file as UTF-8; otherwise the body is the inline prompt text.

When no flag is present, the command SHALL make no mutating request and exit with a non-zero status and a clear message. When only `--var` / `--stage-var` are present, the template and prompts SHALL remain unchanged.

#### Scenario: Replace template from file

- **WHEN** the user runs `mo issue workflow config set 42 --template @wf.yaml`
- **THEN** the CLI reads `wf.yaml` as UTF-8
- **AND** sends `PUT /api/projects/:projectId/issues/42/workflow-profile/template` with the file contents as the body
- **AND** a subsequent `mo issue workflow config get 42` SHALL reflect the new template

#### Scenario: Set variables and stage variables without touching template or prompts

- **WHEN** the user runs `mo issue workflow config set 42 --var foo=bar --stage-var plan.baz=qux`
- **THEN** the CLI sends a variables PATCH containing `foo=bar` and a stage-scoped `plan.baz=qux`
- **AND** the CLI SHALL NOT send any template or prompts request

#### Scenario: Set prompt from inline text and from file

- **WHEN** the user runs `mo issue workflow config set 42 --prompt greeting="You are..."`
- **THEN** the CLI sends `PUT /api/projects/:projectId/issues/42/workflow-profile/prompts/greeting` with the inline body
- **WHEN** the user runs `mo issue workflow config set 42 --prompt greeting=@prompts/greeting.md`
- **THEN** the CLI reads `prompts/greeting.md` as UTF-8
- **AND** sends `PUT /api/projects/:projectId/issues/42/workflow-profile/prompts/greeting` with the file contents as the body

#### Scenario: Composite invocation touches multiple categories

- **WHEN** the user runs `mo issue workflow config set 42 --template @wf.yaml --var foo=bar --prompt greeting="Hi"`
- **THEN** the CLI SHALL issue the template PUT, the variables PATCH, and the prompt PUT
- **AND** each request SHALL be independent

#### Scenario: No flags makes no change

- **WHEN** the user runs `mo issue workflow config set 42` with no flags
- **THEN** the CLI SHALL make no mutating request
- **AND** SHALL exit with a non-zero status and a message stating nothing to change

### Requirement: CLI issue workflow config clear composes template, variable, and prompt removals

`mo issue workflow config clear <num>` SHALL be a composite command that removes only the categories whose flags are present:
- `--template` — sends `DELETE /api/projects/:projectId/issues/:number/workflow-profile/template`, causing the issue to fall back to the project or system template.
- `--var k` — contributes a removal of variable `k` via a variables PATCH that sets `k` to `null`.
- `--prompt <key>` — sends `DELETE /api/projects/:projectId/issues/:number/workflow-profile/prompts/<key>`.

Categories whose flags are absent SHALL NOT be affected. When no flag is present, the command SHALL make no mutating request and exit non-zero with a clear message.

#### Scenario: Clear template falls back to default

- **WHEN** the user runs `mo issue workflow config clear 42 --template`
- **THEN** the CLI sends `DELETE /api/projects/:projectId/issues/42/workflow-profile/template`
- **AND** the issue's template SHALL resolve to the inherited project or system template

#### Scenario: Clear specific variables and prompts without affecting others

- **WHEN** the user runs `mo issue workflow config clear 42 --var foo --prompt greeting`
- **THEN** the CLI sends a variables PATCH setting `foo` to `null`
- **AND** the CLI sends `DELETE /api/projects/:projectId/issues/42/workflow-profile/prompts/greeting`
- **AND** other variables and prompts SHALL remain unchanged

#### Scenario: No flags makes no change

- **WHEN** the user runs `mo issue workflow config clear 42` with no flags
- **THEN** the CLI SHALL make no mutating request
- **AND** SHALL exit with a non-zero status and a message stating nothing to clear

### Requirement: CLI issue workflow config preview renders a prompt

`mo issue workflow config preview <num> <key>` SHALL send `POST /api/projects/:projectId/issues/:number/workflow-profile/prompts/<key>/preview` and print the rendered prompt text. The preview SHALL reflect the issue's current variables and template so the user can diagnose the final prompt an agent would receive.

#### Scenario: Preview renders the final prompt

- **WHEN** the user runs `mo issue workflow config preview 42 plan_prompt`
- **THEN** the CLI sends `POST /api/projects/:projectId/issues/42/workflow-profile/prompts/plan_prompt/preview`
- **AND** SHALL print the rendered prompt text

### Requirement: CLI workflow config surfaces server errors faithfully

When any `mo issue workflow config` subcommand receives a non-success response from the server (for example, an invalid template body), the CLI SHALL print the server-provided error message and exit with a non-zero status, rather than masking the failure or producing partial output.

#### Scenario: Invalid template error is surfaced

- **WHEN** the user runs `mo issue workflow config set 42 --template "@bad.yaml"` and the server rejects the template as invalid
- **THEN** the CLI SHALL print the server error message
- **AND** SHALL exit with a non-zero status

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

### Requirement: CLI provides mo system info command

The CLI SHALL provide `mo system info` as a top-level read-only command that sends `GET /api/system/info` and renders the server's system diagnostics, surfacing the identity (version, git hash, started-at), source (path/branch/head/dirty), install (mode/service-manager/units), update (status/availability), services (server/runner service state), and paths (db/config/logs/opencode) sections returned by the endpoint. The command SHALL accept `-o table|json`. In `-o json` mode the CLI SHALL print the raw server response payload without omission. The command SHALL be distinct from the client-local `mo info` command (which reports the CLI binary's own environment and install source), and `mo system info --help` SHALL disambiguate the two data sources. The `GET /api/system/info` endpoint is global and SHALL NOT require project resolution.

#### Scenario: Table mode renders all diagnostic sections

- **WHEN** the user runs `mo system info -o table`
- **AND** the server returns the full system info payload
- **THEN** the CLI SHALL send `GET /api/system/info`
- **AND** the rendered output SHALL present the identity, source, install, update, services, and paths sections

#### Scenario: JSON mode emits the raw payload

- **WHEN** the user runs `mo system info -o json`
- **THEN** the CLI SHALL print the raw server response payload as JSON

#### Scenario: Server unreachable degrades gracefully

- **WHEN** the user runs `mo system info`
- **AND** the `GET /api/system/info` request fails because the server is not running
- **THEN** the CLI SHALL print a "server not running" notice
- **AND** the CLI SHALL print any locally-derivable diagnostic subset (for example CLI version or install source)
- **AND** the CLI SHALL NOT abort with only a hard error and no diagnostic output

#### Scenario: Help disambiguates from client-local mo info

- **WHEN** the user runs `mo system info --help`
- **THEN** the output SHALL explain that `mo system info` reports server-side system diagnostics
- **AND** SHALL distinguish it from `mo info` which reports the CLI's own local environment

### Requirement: CLI provides mo opencode models command

The CLI SHALL provide `mo opencode models` as a top-level read-only command that lists the available coder model IDs by sending `GET /api/projects/:projectId/opencode/models`. Because the endpoint is project-scoped, the command SHALL resolve the target project via `--project`/`--project-id` (or the active-project fallback), identical to other project-scoped commands. In `-o table` mode the CLI SHALL print exactly one model ID per line so the output can be copied directly into a `--model` flag value. In `-o json` mode the CLI SHALL print the raw server payload, preserving both the `models` array and any model-variant information the server returns. The command SHALL accept `-o table|json`.

#### Scenario: Table mode lists one model ID per line

- **WHEN** the user runs `mo opencode models -o table`
- **AND** the server returns `models: ["anthropic/claude-sonnet", "openai/gpt-5"]`
- **THEN** the CLI SHALL send `GET /api/projects/:projectId/opencode/models`
- **AND** the output SHALL print `anthropic/claude-sonnet` and `openai/gpt-5` on separate lines with no extra per-row decoration

#### Scenario: JSON mode emits the raw payload

- **WHEN** the user runs `mo opencode models -o json`
- **THEN** the CLI SHALL print the raw server payload including the `models` array and any model-variant fields

#### Scenario: Project resolution is required

- **WHEN** the user runs `mo opencode models` with no resolvable project (no `--project`/`--project-id` and no active project)
- **THEN** the CLI SHALL print a clear error explaining no project is resolved
- **AND** SHALL exit with a non-zero status

### Requirement: CLI provides mo runner status online diagnostic command

The CLI SHALL provide `mo runner status` as a read-only command that sends `GET /api/projects/:projectId/runners` and renders a focused online-runner summary: each runner's identifier, last heartbeat timestamp, and idle/busy state (idle when used capacity is zero, busy when used capacity is non-zero). Because the endpoint is project-scoped, the command SHALL resolve the target project via `--project`/`--project-id`. The command SHALL accept `-o table|json`; in `-o json` mode the CLI SHALL print the raw server payload. The command SHALL focus on the online/heartbeat/idle summary and SHALL remain distinct from `mo runner list`, which renders the full-detail runner table (kind/scope/capacity/hostname, etc.).

#### Scenario: Table mode renders online runner summary

- **WHEN** the user runs `mo runner status -o table`
- **AND** the server returns one online idle runner and one online busy runner
- **THEN** the CLI SHALL send `GET /api/projects/:projectId/runners`
- **AND** the rendered output SHALL show each runner's identifier, last heartbeat, and idle/busy state

#### Scenario: JSON mode emits the raw payload

- **WHEN** the user runs `mo runner status -o json`
- **THEN** the CLI SHALL print the raw server runner-status payload

#### Scenario: No runners connected

- **WHEN** the user runs `mo runner status`
- **AND** the server returns an empty runner list
- **THEN** the CLI SHALL report that no runners are connected
- **AND** SHALL exit with status 0

#### Scenario: Project resolution is required

- **WHEN** the user runs `mo runner status` with no resolvable project
- **THEN** the CLI SHALL print a clear error explaining no project is resolved
- **AND** SHALL exit with a non-zero status

### Requirement: CLI runner service-status preserves the service-lifecycle status verb

To resolve the `mo runner status` naming collision in favor of the online-runner diagnostic, the pre-existing service-lifecycle status verb (which reports systemd/scheduled-task unit status for the runner) SHALL be renamed from `mo runner status` to `mo runner service-status`. The renamed `mo runner service-status` command SHALL behave identically to the former `mo runner status` service-lifecycle verb (same flags, same `--dry-run`/`--unit-dir` options, same underlying service-installer status action). The `mo runner --help` output SHALL list `service-status` (not `status`) as the service-lifecycle status command, and SHALL list `status` as the online-runner diagnostic command. This is a breaking rename of the prior `mo runner status` invocation.

#### Scenario: Service-lifecycle status is available under the new name

- **WHEN** the user runs `mo runner service-status`
- **THEN** the CLI SHALL invoke the same service-installer status action that the former `mo runner status` service-lifecycle verb invoked
- **AND** SHALL accept the same `--dry-run` and `--unit-dir` options as the other service-lifecycle verbs

#### Scenario: Runner help lists both verbs with distinct descriptions

- **WHEN** the user runs `mo runner --help`
- **THEN** the output SHALL list `status` described as the online-runner diagnostic
- **AND** SHALL list `service-status` described as the service-lifecycle (systemd/scheduled-task) status

### Requirement: CLI provides mo agent session launch command

The CLI SHALL provide `mo agent session launch <agent>` to launch a generic `AgentSession` from a project-scoped Agent profile. The command SHALL accept the agent identity (name or `agent_*` id) as a positional argument and a required `--prompt <text>` flag (or `--prompt-file <path>` / `--prompt-stdin` for long prompts) carrying the user's prompt. The command SHALL send `POST /api/projects/:projectId/agents/:agentId/sessions` with the prompt (and optional context) and SHALL print the new session id, the agent id/name, and the current session status. The command SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: Launch prints the new session id and status

- **WHEN** the user runs `mo agent session launch reviewer --prompt "Audit the auth flow"`
- **THEN** the CLI resolves `reviewer` to an agent id in the project
- **AND** sends `POST /api/projects/:projectId/agents/:agentId/sessions` with the prompt
- **AND** prints the new session id, the agent id/name, and the current session status

#### Scenario: Launch reads the prompt from a file

- **WHEN** the user runs `mo agent session launch reviewer --prompt-file task.md`
- **THEN** the CLI reads `task.md` as UTF-8 text
- **AND** sends the file contents as the prompt in the launch request body

#### Scenario: Launch reads the prompt from stdin

- **WHEN** the user runs `echo "summarize this" | mo agent session launch reviewer --prompt-stdin`
- **THEN** the CLI reads the prompt from standard input
- **AND** sends the stdin contents as the prompt in the launch request body

#### Scenario: Launch in JSON emits the raw payload

- **WHEN** the user runs `mo agent session launch reviewer --prompt "Hi" -o json`
- **THEN** the CLI SHALL print the server response as JSON

#### Scenario: Missing prompt fails clearly

- **WHEN** the user runs `mo agent session launch reviewer` without `--prompt`, `--prompt-file`, or `--prompt-stdin`
- **THEN** the CLI prints a clear validation error
- **AND** exits with code 1

#### Scenario: Unknown agent surfaces server error

- **WHEN** the user runs `mo agent session launch nope --prompt "Hi"`
- **AND** the server returns `404` because agent `nope` does not resolve in the project
- **THEN** the CLI prints the server-provided error message
- **AND** does not report silent success
- **AND** exits with a non-zero status

### Requirement: CLI provides mo agent session followup command

The CLI SHALL provide `mo agent session followup <sessionId>` to send a free-text followup to a running generic `AgentSession`. The command SHALL accept the session id as a positional argument and a required `--text <text>` flag (or `--text-file <path>` / `--text-stdin` for long messages) carrying the followup text. The command SHALL send `POST /api/projects/:projectId/agent-sessions/:sessionId/followup` with the text and SHALL print the delivery status. The command SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: Followup prints the delivery status

- **WHEN** the user runs `mo agent session followup sess_123 --text "add a logout route"`
- **THEN** the CLI sends `POST /api/projects/:projectId/agent-sessions/sess_123/followup` with the text
- **AND** prints the delivery status returned by the server

#### Scenario: Followup reads the text from a file

- **WHEN** the user runs `mo agent session followup sess_123 --text-file note.md`
- **THEN** the CLI reads `note.md` as UTF-8 text
- **AND** sends the file contents as the followup text

#### Scenario: Followup in JSON emits the raw payload

- **WHEN** the user runs `mo agent session followup sess_123 --text "Hi" -o json`
- **THEN** the CLI SHALL print the server response as JSON

#### Scenario: Terminal session surfaces conflict

- **WHEN** the user runs `mo agent session followup sess_123 --text "Hi"`
- **AND** the server returns `409` because the session is no longer active
- **THEN** the CLI prints the server-provided error message
- **AND** SHALL NOT report success
- **AND** exits with a non-zero status

#### Scenario: Missing text fails clearly

- **WHEN** the user runs `mo agent session followup sess_123` without `--text`, `--text-file`, or `--text-stdin`
- **THEN** the CLI prints a clear validation error
- **AND** exits with code 1

### Requirement: CLI provides mo agent session cancel command

The CLI SHALL provide `mo agent session cancel <sessionId>` to request cancellation of a running generic `AgentSession` by sending `POST /api/projects/:projectId/agent-sessions/:sessionId/cancel`. The command SHALL print the resulting session state returned by the server. When the server reports the session is not currently cancellable, the CLI SHALL surface that state to the user rather than reporting success. When the server reports the session is already terminal, the CLI SHALL surface the terminal state. The command SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: Cancel prints the resulting session state

- **WHEN** the user runs `mo agent session cancel sess_123`
- **AND** the server cancels the running turn
- **THEN** the CLI prints the resulting session state returned by the server

#### Scenario: Non-cancellable session is surfaced honestly

- **WHEN** the user runs `mo agent session cancel sess_123`
- **AND** the server reports the session is not currently cancellable
- **THEN** the CLI SHALL surface that state to the user
- **AND** SHALL NOT report success

#### Scenario: Terminal session surfaces terminal state

- **WHEN** the user runs `mo agent session cancel sess_123`
- **AND** the server reports the session is already in a terminal state
- **THEN** the CLI SHALL surface the terminal state
- **AND** SHALL NOT report a fresh cancellation

#### Scenario: Unknown session surfaces server error

- **WHEN** the user runs `mo agent session cancel nope`
- **AND** the server returns `404` because session `nope` does not exist
- **THEN** the CLI prints the server-provided error message
- **AND** exits with a non-zero status

### Requirement: CLI provides mo agent session list command

The CLI SHALL provide `mo agent session list <agent>` to list the generic `AgentSession`s belonging to a project-scoped Agent profile. The command SHALL accept the agent identity (name or `agent_*` id) as a positional argument and SHALL send `GET /api/projects/:projectId/agents/:agentId/sessions`, honoring an optional `--status <status>` flag that filters the result (covering at least `running`, `completed`, `failed`, `stopped`). In `-o table` mode the CLI SHALL render one row per session, surfacing at least the session id, status, created time, and resolved model, and SHALL group or annotate rows so the user can distinguish running, failed, and ended sessions. In `-o json` mode the CLI SHALL print the raw server payload without omission. The command SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: List prints an agent's sessions

- **WHEN** the user runs `mo agent session list reviewer`
- **THEN** the CLI resolves `reviewer` to an agent id in the project
- **AND** sends `GET /api/projects/:projectId/agents/:agentId/sessions`
- **AND** prints one row per session including at least the session id, status, created time, and resolved model

#### Scenario: List with a status filter

- **WHEN** the user runs `mo agent session list reviewer --status failed`
- **THEN** the CLI sends `GET /api/projects/:projectId/agents/:agentId/sessions?status=failed`
- **AND** prints only that agent's failed sessions

#### Scenario: List in JSON emits the raw payload

- **WHEN** the user runs `mo agent session list reviewer -o json`
- **THEN** the CLI SHALL print the raw server response payload as JSON

#### Scenario: List unknown agent surfaces server error

- **WHEN** the user runs `mo agent session list nope`
- **AND** the server returns `404` because agent `nope` does not resolve in the project
- **THEN** the CLI prints the server-provided error message
- **AND** does not report silent success
- **AND** exits with a non-zero status

### Requirement: CLI provides mo agent session show command

The CLI SHALL provide `mo agent session show <sessionId>` to read the summary of a generic `AgentSession`. The command SHALL accept the session id as a positional argument and SHALL send `GET /api/projects/:projectId/agent-sessions/:sessionId`. In `-o table` mode the CLI SHALL render a human-readable summary surfacing the agent id and agent name, status, created and last-activity times, resolved model, usage, failure category (when present), tool call and tool error counts, and any recorded context references (issue, epic, repository, workspace path). In `-o json` mode the CLI SHALL print the raw server payload without omission. The command SHALL accept `--project/--project-id` and `-o table|json`. When the session does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status. The command SHALL be distinct from the existing `mo issue session show <num> <name>` workflow-session verb.

#### Scenario: Show renders the generic session summary

- **WHEN** the user runs `mo agent session show sess_123 -o table`
- **THEN** the CLI sends `GET /api/projects/:projectId/agent-sessions/sess_123`
- **AND** the rendered output SHALL present the agent identity, status, created and last-activity times, resolved model, usage, failure category (when present), tool call and tool error counts, and recorded context references

#### Scenario: Show in JSON emits the raw payload

- **WHEN** the user runs `mo agent session show sess_123 -o json`
- **THEN** the CLI SHALL print the server response as JSON

#### Scenario: Show nonexistent session surfaces error

- **WHEN** the user runs `mo agent session show nope`
- **AND** the server returns `404` for session `nope`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status

#### Scenario: Show is distinct from the workflow session verb

- **WHEN** the user runs `mo agent session show <sessionId>`
- **THEN** the CLI SHALL target the generic-session summary endpoint
- **AND** SHALL NOT invoke the existing `mo issue session show <num> <name>` workflow-session verb
- **AND** the existing workflow-session verb SHALL remain unchanged

### Requirement: CLI provides mo agent session transcript command

The CLI SHALL provide `mo agent session transcript <sessionId>` to read the transcript of a generic `AgentSession`. The command SHALL accept the session id as a positional argument and SHALL send `GET /api/projects/:projectId/agent-sessions/:sessionId/transcript`. In `-o table` mode the CLI SHALL render a summary (turn count / part count, first and last activity timestamps) rather than dumping every message, consistent with `mo issue session transcript` table-mode behavior. In `-o json` mode the CLI SHALL print the full transcript in its raw server JSON shape. The command SHALL accept `--project/--project-id` and `-o table|json`. When the session does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Transcript table mode renders a summary

- **WHEN** the user runs `mo agent session transcript sess_123 -o table`
- **THEN** the CLI sends `GET /api/projects/:projectId/agent-sessions/sess_123/transcript`
- **AND** the rendered output SHALL present a summary including the part count and first/last activity timestamps
- **AND** SHALL NOT dump every individual message body

#### Scenario: Transcript JSON mode emits the full transcript

- **WHEN** the user runs `mo agent session transcript sess_123 -o json`
- **THEN** the CLI SHALL print the full transcript payload as returned by the server

#### Scenario: Transcript nonexistent session surfaces error

- **WHEN** the user runs `mo agent session transcript nope`
- **AND** the server returns `404` for session `nope`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status
