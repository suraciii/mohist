### Requirement: Agent list page

The Web shell SHALL expose a new top-level "Agents" navigation entry under the project scope that opens the Agent list page. The list SHALL enumerate every Agent profile in the current project (active and archived, distinguishable) and, for each profile, SHALL surface a runtime/config summary (agent type, model, variant), the profile's most recent session, and an availability status reflecting whether the profile can currently be launched (e.g. archived, no available runner). The list SHALL be consumable from the #130 agent-scoped listing API and SHALL present a clear empty state when no Agent profiles exist in the project.

#### Scenario: Agents nav entry opens the list

- **WHEN** a user opens the project shell
- **THEN** an "Agents" entry SHALL be present in the primary navigation
- **AND** selecting it SHALL navigate to the Agent list page scoped to the current project

#### Scenario: Each profile row shows summary, recent session, and availability

- **WHEN** the Agent list renders one or more profiles
- **THEN** each row SHALL display the profile name, agent type, model, and variant
- **AND** SHALL display the profile's most recent session
- **AND** SHALL display an availability status indicating whether the profile can be launched

#### Scenario: No profiles defined

- **WHEN** the project has no Agent profiles
- **THEN** the list SHALL render an empty state that explains no agents are defined and offers an entry to create one

### Requirement: Agent detail page

Selecting a profile from the Agent list SHALL open the Agent detail page, which SHALL show the profile summary (instructions, agent config, skills metadata) and the profile's session history grouped by lifecycle state into at least recent, running, failed, and ended sections. The detail page SHALL provide an entry point to start a new session from this profile and an entry point to edit or archive the profile. Archived profiles SHALL be clearly marked and their launch entry SHALL be disabled.

#### Scenario: Detail shows profile summary and grouped session history

- **WHEN** a user opens an Agent profile detail page
- **THEN** the page SHALL render the profile's instructions, agent config (model + variant), and skills metadata
- **AND** SHALL render the profile's sessions grouped into recent, running, failed, and ended lifecycle sections

#### Scenario: Detail offers a new-session entry

- **WHEN** a user views an active (non-archived) Agent profile detail page
- **THEN** the page SHALL offer an entry point that opens the new-session composer pre-selected to that profile

#### Scenario: Archived profile disables launch

- **WHEN** a user views an archived Agent profile detail page
- **THEN** the profile SHALL be clearly marked as archived
- **AND** the new-session entry SHALL be disabled

### Requirement: Agent profile management

The workbench SHALL let a user create, edit, and archive an Agent profile through a dedicated editor. The editor SHALL capture `instructions`, `agentConfig` (model and variant selected through the unified `ModelSelect` widget), and `skills` metadata, and SHALL persist changes through the #128 CRUD API. The editor SHALL validate required fields before submission and SHALL surface API errors inline.

#### Scenario: Create a new profile

- **WHEN** a user opens the profile editor with no existing profile and submits valid instructions, model, variant, and skills metadata
- **THEN** the workbench SHALL create the profile via the #128 CRUD API
- **AND** SHALL navigate the user to the new profile's detail page

#### Scenario: Edit an existing profile

- **WHEN** a user edits an existing profile's instructions, agent config, or skills metadata and saves
- **THEN** the workbench SHALL persist the change via the #128 CRUD API
- **AND** SHALL reflect the updated values on the detail page

#### Scenario: Archive a profile

- **WHEN** a user archives an Agent profile
- **THEN** the workbench SHALL mark the profile as archived via the #128 CRUD API
- **AND** the profile SHALL remain listed but clearly distinguished from active profiles
- **AND** the profile SHALL NOT be launchable from the UI

#### Scenario: Invalid submission is blocked

- **WHEN** a user submits the editor with missing required fields
- **THEN** the editor SHALL NOT submit
- **AND** SHALL surface validation errors inline on the offending fields

### Requirement: New session composer

The workbench SHALL provide a new-session composer that lets a user pick an Agent profile, enter a prompt, and optionally attach context references. Context references SHALL be limited to issue, epic, project, repository, and workspace path. Context references SHALL be carried as session metadata only and SHALL NOT create workflow scope, mount configuration, or supervisor lifecycle. Launching a session SHALL invoke the #129 launch endpoint and SHALL navigate the user to the resulting generic session detail page.

#### Scenario: Compose and launch a session

- **WHEN** a user selects an Agent profile, enters a prompt, and launches
- **THEN** the workbench SHALL call the #129 launch endpoint with the profile, prompt, and any context references
- **AND** SHALL navigate to the resulting session detail page

#### Scenario: Optional context references are metadata only

- **WHEN** a user attaches one or more context references (issue, epic, project, repository, or workspace path) to a new session
- **THEN** those references SHALL be passed as session metadata
- **AND** SHALL NOT cause the creation of workflow scope, mount configuration, or supervisor lifecycle

#### Scenario: Prompt is required to launch

- **WHEN** a user attempts to launch without entering a prompt
- **THEN** the composer SHALL NOT submit
- **AND** SHALL indicate that a prompt is required

### Requirement: Follow-up input for direct sessions

For a generic AgentSession in a non-terminal (running or recoverable) state, the workbench SHALL present a follow-up composer that sends a follow-up prompt to that session via the #129 follow-up endpoint. For a session in a terminal state (completed or failed), the follow-up composer SHALL be disabled or hidden.

#### Scenario: Send a follow-up to an active session

- **WHEN** a user views a generic session in a running or recoverable state and submits a follow-up prompt
- **THEN** the workbench SHALL send the follow-up via the #129 follow-up endpoint
- **AND** the transcript SHALL reflect the follow-up and subsequent agent activity

#### Scenario: Follow-up disabled on terminal session

- **WHEN** a user views a generic session in a completed or failed state
- **THEN** the follow-up composer SHALL be disabled or hidden

### Requirement: Generic session detail entry

The workbench SHALL allow a generic (non-workflow) `AgentSession` to be opened by its session id at a dedicated route (e.g. `agent-sessions/:id`). The detail page SHALL render the session transcript, status, usage, failure category (when failed), and any context references, consuming the #130 generic-session summary and transcript endpoints. The detail page SHALL NOT require an owning issue or workflow stage to render.

#### Scenario: Open a generic session by id

- **WHEN** a user navigates to a generic session's route by session id
- **THEN** the workbench SHALL resolve and render the session via the #130 generic-session endpoints
- **AND** SHALL display the transcript, status, usage, and any context references
- **AND** SHALL NOT require an owning issue or workflow stage

#### Scenario: Failed session shows failure category

- **WHEN** a user opens a generic session that ended in a failed state
- **THEN** the detail page SHALL surface the failure category

### Requirement: "Ask Agent" quick entry

The issue detail page, epic detail page, and project context SHALL each expose an "Ask Agent" quick entry that opens the new-session composer with the current entity pre-filled as a context reference. The quick entry SHALL only pre-fill context metadata and SHALL NOT create supervisor, mount, or workflow configuration.

#### Scenario: Ask Agent from an issue

- **WHEN** a user selects "Ask Agent" on an issue detail page
- **THEN** the workbench SHALL open the new-session composer
- **AND** SHALL pre-fill the current issue as a context reference
- **AND** SHALL NOT create any supervisor, mount, or workflow configuration

#### Scenario: Ask Agent from an epic

- **WHEN** a user selects "Ask Agent" on an epic detail page
- **THEN** the workbench SHALL open the new-session composer with the current epic pre-filled as a context reference

#### Scenario: Ask Agent from a project

- **WHEN** a user selects "Ask Agent" from the project context
- **THEN** the workbench SHALL open the new-session composer with the current project pre-filled as a context reference

### Requirement: Workbench empty and error states

The workbench SHALL surface dedicated empty and error states for conditions that block normal use: no Agent profiles defined, no available runner for the selected agent type, an external agent being unavailable, the selected profile being archived, and a session being in a running, failed, or completed state. Each state SHALL be communicated clearly to the user rather than rendering a blank or broken surface.

#### Scenario: No available runner

- **WHEN** a user attempts to launch a session for an agent type with no available runner
- **THEN** the workbench SHALL surface a clear message that no runner is available for that agent type
- **AND** SHALL NOT attempt the launch

#### Scenario: External agent unavailable

- **WHEN** the selected agent's external runtime is unavailable
- **THEN** the workbench SHALL surface a clear message that the external agent is unavailable
- **AND** SHALL prevent launching until it recovers

#### Scenario: Session lifecycle states are communicated

- **WHEN** a user views a generic session that is running, failed, or completed
- **THEN** the workbench SHALL clearly communicate that lifecycle state in the session detail
