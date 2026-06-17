## ADDED Requirements

### Requirement: Workflow profiles store agent config as ordinary Variables

`ProjectWorkflowProfile` and `IssueWorkflowProfile` SHALL store agent configuration exclusively inside a shared `VariableBundle` (`vars` + `stages`). The agent key (`vars.agent` and `stages.<stage>.vars.agent`) SHALL be an ordinary variable: no dedicated `AgentConfig` or `StageAgentConfigs` member, no agent-specific merge branch, and no agent-specific dispatch code. Any workflow profile scope that carries agent overrides SHALL use the identical `VariableBundle` shape so reads, writes, and merges are symmetric across scopes.

#### Scenario: Project profile stores selected model as an ordinary variable

- **WHEN** the Coder Agent Tab writes a selected model into a project workflow profile
- **THEN** the value SHALL be stored at `Variables.vars.agent`
- **AND** the profile SHALL NOT expose a dedicated `agentConfig` or `stageAgentConfigs` field for that value

#### Scenario: Issue profile uses the same bundle shape as the project profile

- **WHEN** an issue workflow profile carries an agent override
- **THEN** the override SHALL live at `Variables.vars.agent` or `Variables.stages.<stage>.vars.agent`
- **AND** the `VariableBundle` type SHALL be identical to the project profile's bundle type

#### Scenario: No agent-specific code path exists

- **WHEN** profile code reads, writes, or merges variables
- **THEN** the `agent` key SHALL be treated identically to any other variable key
- **AND** no profile method signature SHALL accept an agent-specific parameter such as `globalAgentConfig` or `globalStageAgentConfigs`

### Requirement: Issue creation merges project and global Variables generically

At issue creation (T1), the system SHALL produce the issue workflow profile's effective `Variables` by generically merging the project profile's `Variables` over the global `Variables`. The merge SHALL be symmetric: top-level `vars` and each `stages.<stage>.vars` SHALL use the same precedence rule, where project values win and global values fill gaps. The merge SHALL NOT special-case the `agent` key.

#### Scenario: Project vars override global vars

- **WHEN** a variable key exists in both the project `vars` and the global `vars`
- **THEN** the merged issue `vars` SHALL contain the project value for that key

#### Scenario: Global vars fill gaps not set by project

- **WHEN** a variable key exists only in the global `vars`
- **THEN** the merged issue `vars` SHALL contain the global value for that key

#### Scenario: Per-stage merge mirrors top-level merge

- **WHEN** a stage has variables defined at both project and global layers
- **THEN** the merged `stages.<stage>.vars` SHALL apply the same project-over-global precedence as top-level `vars`
- **AND** the merge code path for `stages.<stage>.vars` SHALL be identical in shape to the `vars` merge

#### Scenario: Agent key is not special during merge

- **WHEN** the merged `vars.agent` is computed
- **THEN** it SHALL be produced by the same generic variable merge as every other key
- **AND** no agent-specific branch SHALL exist in the merge implementation

#### Scenario: Later global config changes apply to newly created issues

- **WHEN** global `config.jsonc` variables change after some issues already exist
- **THEN** issues created after the change SHALL reflect the new global values in their T1-merged `Variables`
- **AND** already-created issues SHALL retain their previously merged `Variables`

### Requirement: Global configuration is expressed as a VariableBundle

`config.jsonc` SHALL be exposed in memory as a `VariableBundle` whose `vars` contains the global `agent` variable and whose `stages` is always empty, because stage names are project-specific and cannot be configured globally.

#### Scenario: config.jsonc agent maps to vars.agent

- **WHEN** the global config defines an agent model
- **THEN** the in-memory `VariableBundle` SHALL expose it at `vars.agent`
- **AND** `stages` SHALL be empty

#### Scenario: Global config never carries stage variables

- **WHEN** the global `VariableBundle` is constructed
- **THEN** `stages` SHALL be an empty map regardless of `config.jsonc` content

### Requirement: Existing IssueWorkflowProfile agent data migrates into Variables

A one-way data migration SHALL move any existing issue workflow profile agent data from agent-specific fields into the shared `VariableBundle`: `AgentConfig` SHALL move to `Variables.vars.agent` and `StageAgentConfigs` SHALL move to `Variables.stages.<stage>.vars.agent`. The migration SHALL be reversible on failure and SHALL NOT destroy the source data until the migrated `Variables` has been validated.

#### Scenario: AgentConfig migrates to vars.agent

- **WHEN** an existing issue workflow profile has an `AgentConfig` value
- **THEN** the migration SHALL write that value to `Variables.vars.agent`
- **AND** the migrated bundle SHALL validate before the source field is cleared

#### Scenario: StageAgentConfigs migrate to stages

- **WHEN** an existing issue workflow profile has per-stage agent configs
- **THEN** the migration SHALL write each stage's config to `Variables.stages.<stage>.vars.agent`
- **AND** the migration SHALL be reversible if any stage fails to validate
