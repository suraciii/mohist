### Requirement: Canonical command forms and verbs
`mo` SHALL expose resource commands only as `mo <area> [<subarea>] <action> [target] [flags]` and direct task commands only as `mo <task> [target] [flags]`. Resource reads MUST use `list` for collections and `view` for individual resources; resource mutation MUST use `create`, `edit`, or `delete`; relationship changes MUST use `add` or `remove`; and recoverable archival MUST use `archive` or `restore`. `get`, `set`, and `unset` MUST be reserved for defined key-value operations. Domain state transitions MUST retain their domain verbs.

#### Scenario: Reading and editing an Issue
- **WHEN** a user needs the current state of Issue 42 or changes its title
- **THEN** the CLI SHALL accept `mo issue view 42` and `mo issue edit 42 --title <title>`
- **AND THEN** the CLI MUST NOT expose `show` or `update` as alternate Issue actions

#### Scenario: Recovering an archived resource
- **WHEN** a user restores an archived resource
- **THEN** the CLI SHALL expose the `restore` action for that resource
- **AND THEN** the CLI MUST NOT expose `unarchive` as an alias

### Requirement: One command path per owned intent
Each product intent SHALL have exactly one command path determined by the user-facing resource or relationship it changes. A target selector MUST provide alternate addressing only and MUST NOT duplicate an action under another resource.

#### Scenario: Controlling a WorkflowRun from an Issue number
- **WHEN** a user approves, retries, reruns, pauses, resumes, stops, or rejects the Run currently bound to Issue 42
- **THEN** the CLI SHALL use the corresponding `mo run <action> --issue 42` command
- **AND THEN** the CLI MUST NOT expose the same Run control action under `mo issue` or `mo workflow`

#### Scenario: Reading a Session by its origin
- **WHEN** a user needs a Session created by an Issue or an Agent
- **THEN** the CLI SHALL use `mo session list --issue <number>` or `mo session list --agent <agent>` to locate it and the stable Session ID for subsequent operations
- **AND THEN** the CLI MUST NOT expose separate Issue-scoped or Agent-scoped Session command trees

#### Scenario: Managing a Workflow Profile selection
- **WHEN** a user sets a Project default Workflow Profile or an Issue-specific Profile
- **THEN** the CLI SHALL use `mo project workflow set-default <profile>` or the `--workflow-profile` input of `mo issue create` or `mo issue edit`
- **AND THEN** the CLI MUST NOT expose those selection changes as Workflow Profile collection actions

### Requirement: Canonical command areas
The root command tree SHALL use the canonical areas and tasks: `project`, `repo`, `issue`, `epic`, `label`, `workflow`, `run`, `agent`, `session`, `activity`, `routing`, `runner`, `server`, `service`, `event`, `notification`, `otel`, `skill`, `help`, `install`, `update`, and `info`. Each area MUST expose only actions with an independent product behavior in that area.

#### Scenario: Accessing built-in coder agent skills
- **WHEN** a user lists or reads a packaged coder agent skill
- **THEN** the CLI SHALL expose `mo skill list` and `mo skill view <name>`
- **AND THEN** the CLI MUST NOT expose a plural `skills` command group or a `skill get` resource-read synonym

#### Scenario: Accessing runtime-related configuration
- **WHEN** a user needs an Agent model catalog
- **THEN** the CLI SHALL expose it as `mo agent model list --runtime <runtime>`
- **AND THEN** the CLI MUST NOT expose a top-level runtime or `opencode` area

#### Scenario: Managing configuration with an explicit owner
- **WHEN** a user changes a Project Variable, Prompt, Agent setting, or Run Variable
- **THEN** the CLI SHALL use the command owned by that resource
- **AND THEN** the CLI MUST NOT expose a generic top-level `config` command group

### Requirement: Canonical scope, input, and output vocabulary
Every Project-scoped command SHALL resolve an explicit `--project <name-or-id>` before a project resolved from the current directory and then the configured current Project. The CLI MUST reject `--project-id`, MUST reject mutually exclusive inputs locally, and MUST use `--<name>-file -` or `--file -` as the only stdin form for long text or documents. Resource-producing commands MUST use `--json <field,...>` for field selection and MUST NOT expose a generic output-format option.

#### Scenario: Selecting a Project by its ID
- **WHEN** a user supplies a Project ID to an Issue command
- **THEN** the CLI SHALL accept it through `--project <name-or-id>`
- **AND THEN** `--project-id` MUST fail as a usage error without issuing a remote request

#### Scenario: Discovering available JSON fields
- **WHEN** a user invokes a resource command with `--json` and no field list
- **THEN** the CLI SHALL print that command's available fields and exit successfully without performing the resource operation

#### Scenario: Requesting a selected resource projection
- **WHEN** a user invokes `mo issue list --json number,title,status`
- **THEN** stdout SHALL contain only a JSON array with those selected fields
- **AND THEN** the CLI MUST NOT emit an output envelope, progress text, or a generic `--output` mode
