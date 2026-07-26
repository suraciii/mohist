### Requirement: Mohist Skill is a decision entry point
The packaged `mohist` Skill SHALL guide Agents to select a Mohist scenario and obtain current facts before acting. Its frontmatter MUST identify when the Skill applies, and its body MUST cover scope, first read, scenario routing, hard decisions, and CLI handoff in progressively disclosed form.

#### Scenario: Receiving an existing Issue context
- **WHEN** an Agent receives an existing Issue number or WorkflowRun context
- **THEN** the Mohist Skill SHALL direct it to read the current Issue or Run state with the canonical CLI before interpreting or mutating that work

#### Scenario: Selecting an issue-creation workflow
- **WHEN** an Agent needs to create a new Issue
- **THEN** the Mohist Skill SHALL direct it to load the dedicated issue-creation Skill
- **AND THEN** it MUST NOT embed the complete issue-creation procedure in the entry Skill

### Requirement: Skill provides Mohist-specific decisions
The Mohist Skill SHALL explain only decisions that cannot be reliably inferred from general CLI conventions, including the distinction between `retry` and `rerun`, `pause` and `stop`, and `compact` and `reset`. It MUST direct Agents to leaf help for exact arguments and current flag syntax.

#### Scenario: Recovering a failed Run
- **WHEN** an Agent must choose between retrying the current failure and rerunning prior work
- **THEN** the Mohist Skill SHALL distinguish `retry` from `rerun` and its `--from-stage` variant
- **AND THEN** it SHALL direct the Agent to the relevant leaf help before constructing the invocation

#### Scenario: Stopping a Run
- **WHEN** an Agent considers ending a Run
- **THEN** the Mohist Skill SHALL distinguish resumable `pause` from terminal `stop`
- **AND THEN** it MUST NOT state a stale or duplicated list of command flags

### Requirement: Skill defers syntax and implementation detail
The packaged entry Skill SHALL not duplicate the full command tree, lifecycle tables, shared flags, output-format reference, service startup commands, test commands, source paths, removed implementations, or compatibility history. Command examples included in the Skill MUST use canonical paths and parse against the current command tree.

#### Scenario: CLI syntax changes after Skill packaging
- **WHEN** a command's available flags change in a later CLI version
- **THEN** the Mohist Skill SHALL continue to direct the Agent to `mo <command> --help` for the exact syntax
- **AND THEN** the entry Skill MUST NOT contain a copied flag inventory that can diverge from the binary

#### Scenario: Installing or operating a local service
- **WHEN** an Agent needs exact installation or service-management syntax
- **THEN** the Mohist Skill SHALL hand off to the relevant command help
- **AND THEN** it MUST NOT prescribe server, runner, Web, or test startup commands

### Requirement: Skill examples remain executable and bounded
Every command example in the packaged Mohist Skill SHALL use a canonical command path and SHALL be parseable by the command tree. The Skill MUST use a sibling Skill or reference only for a real branching scenario and MUST NOT create layers that do not correspond to a distinct decision.

#### Scenario: Verifying packaged Skill guidance
- **WHEN** the CLI validates the packaged Mohist Skill
- **THEN** every command example SHALL parse successfully against the same command tree that produces help
- **AND THEN** a removed alias or obsolete command area in the Skill MUST fail validation
