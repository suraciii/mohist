## ADDED Requirements

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
