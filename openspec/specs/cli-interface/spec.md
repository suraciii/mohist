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
