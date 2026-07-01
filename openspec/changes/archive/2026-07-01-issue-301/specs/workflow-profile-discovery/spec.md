## ADDED Requirements

### Requirement: Per-project disabled-profile blacklist

A project's `ProjectWorkflowProfile` SHALL maintain a per-project disabled-profile blacklist. The blacklist SHALL default to empty, meaning every system workflow profile is enabled by default. The system default `mohist/local` SHALL NOT be specially protected — it MAY be disabled like any other system profile, and disabling it is the explicit way to exclude an unused system profile from a project. The blacklist SHALL be the single source of truth for which system profiles are enabled for a project.

#### Scenario: New project has all profiles enabled

- **WHEN** a new project is created
- **THEN** the project's disabled-profile blacklist SHALL be empty
- **AND** every system workflow profile (including `mohist/local`) SHALL be enabled for that project

#### Scenario: mohist/local is not specially protected

- **WHEN** an operator disables `mohist/local` for a project that has at least one other enabled profile
- **THEN** `mohist/local` SHALL be added to the project's disabled-profile blacklist
- **AND** no special-casing logic SHALL re-enable `mohist/local` as an unconditional fallback

#### Scenario: Disabling one of several enabled profiles succeeds

- **WHEN** an operator disables a profile for a project that has more than one enabled profile
- **THEN** the disable action SHALL succeed
- **AND** the profile SHALL be added to the project's disabled-profile blacklist

### Requirement: Project-scoped discovery filters disabled profiles

The workflow discovery surface SHALL filter out disabled profiles for the target project before returning the catalog. The HTTP endpoints `/api/workflow-templates/system` and `/api/workflow-profiles` SHALL accept project context (project id/ref) and SHALL NOT include any profile id that is on the target project's disabled-profile blacklist. The filtered result SHALL be the only catalog the agent and CLI consume.

#### Scenario: HTTP discovery excludes disabled profiles

- **WHEN** a client requests `/api/workflow-templates/system` or `/api/workflow-profiles` for a project whose blacklist contains `mohist/github-pr`
- **THEN** the response SHALL NOT include `mohist/github-pr`
- **AND** the response SHALL include every system profile that is not on the blacklist

#### Scenario: Discovery returns the full catalog when the blacklist is empty

- **WHEN** a client requests the discovery endpoints for a project whose blacklist is empty
- **THEN** the response SHALL include every system workflow profile

#### Scenario: Discovery reflects a freshly disabled profile

- **WHEN** an operator disables `mohist/local` for a project and a client immediately requests the discovery endpoints for that project
- **THEN** the response SHALL NOT include `mohist/local`

### Requirement: CLI and agent discovery honor the enabled set

`mo workflow list --described` (which backs the `mohist-create-issue` agent candidate list) SHALL resolve the current project and consume the filtered discovery endpoint, so the agent never recommends a profile the project has disabled. The bundled `mohist-create-issue` skill SHALL describe the fallback semantics as "the first enabled profile, else fail with an actionable error" and SHALL NOT state that `mohist/local` is an unconditional fallback.

#### Scenario: mo workflow list reflects the enabled set

- **WHEN** an operator runs `mo workflow list --described` against a project that has disabled `mohist/github-pr`
- **THEN** the output SHALL NOT list `mohist/github-pr`
- **AND** the output SHALL list every enabled system profile

#### Scenario: Agent candidate list never recommends disabled profiles

- **WHEN** the `mohist-create-issue` agent builds its workflow candidate list for a project
- **THEN** the candidate list SHALL contain only profiles in the project's enabled set
- **AND** the agent SHALL NOT recommend a profile that is on the project's disabled-profile blacklist

### Requirement: At least one enabled profile per project

Every project SHALL keep at least one enabled profile. Disabling the last remaining enabled profile SHALL be rejected at the action boundary with a clear consequence message that explains the project must retain at least one enabled workflow. The system SHALL NOT permit a project to reach a state of zero enabled profiles through the disable action.

#### Scenario: Disabling the last enabled profile is rejected

- **WHEN** an operator attempts to disable the only remaining enabled profile for a project
- **THEN** the disable action SHALL be rejected
- **AND** the project's disabled-profile blacklist SHALL remain unchanged
- **AND** the error SHALL name the consequence that the project must retain at least one enabled workflow

#### Scenario: Last-enabled rejection names the consequence

- **WHEN** an operator attempts to disable the last enabled profile and the action is rejected
- **THEN** the error message SHALL be actionable and SHALL explain that at least one workflow must stay enabled

### Requirement: Issue creation requires at least one enabled profile

Issue creation SHALL be rejected with an actionable error when the target project has zero enabled profiles, rather than silently resolving to a disabled default. The error SHALL instruct the operator to enable a workflow first. This pre-flight check SHALL run before any issue is persisted.

#### Scenario: Issue creation rejected when no profile is enabled

- **WHEN** a client creates an issue in a project whose disabled-profile blacklist contains every system profile
- **THEN** the server SHALL reject the creation with an actionable error
- **AND** the error SHALL instruct the operator to enable a workflow first
- **AND** no issue SHALL be persisted

#### Scenario: Issue creation proceeds when at least one profile is enabled

- **WHEN** a client creates an issue in a project that has at least one enabled profile
- **THEN** the creation SHALL proceed normally
- **AND** the issue's effective profile SHALL never resolve to a disabled profile
