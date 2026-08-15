### Requirement: The composer is task-first

The Web session composer SHALL present the task before any Agent concept: the
prompt with its attachments and context references come first, and Agent
selection is optional. With no Agent selected, launching SHALL create a new
Agent for the task through the task-first create-and-launch operation;
selecting an existing Agent SHALL submit the definition-first launch of that
Agent. Launch SHALL be enabled when the composed task is usable and the
execution configuration for the selected path is resolvable.

#### Scenario: A task alone launches

- **WHEN** the user enters only a prompt, the execution configuration is resolvable, and the user launches
- **THEN** the composer submits the task-first create-and-launch request without requiring an Agent selection

#### Scenario: Selecting an existing Agent keeps the definition-first path

- **WHEN** the user selects an existing Agent and launches
- **THEN** the composer submits the definition-first launch for that Agent
- **AND** the composer does not override the selected Agent's execution definition

### Requirement: Inline execution configuration when no Project default exists

When the composer would create a new Agent and the Project has no default
execution configuration, the composer SHALL collect the execution
configuration inline — Runtime, Model, and optional Variant — before launch
and submit it as the request's execution hints; it MUST NOT dead-end in the
Agent settings page. When a Project default exists, the composer SHALL NOT
ask about execution configuration and SHALL launch on the defaults. The
inline controls apply only to the create-new path; a selected existing Agent
keeps its own configuration.

#### Scenario: No default asks inline

- **WHEN** the Project has no default execution configuration and the user launches a task without selecting an Agent
- **THEN** the composer requires an inline Runtime and Model before launch and submits them as execution hints

#### Scenario: A default launches without questions

- **WHEN** the Project has a default execution configuration and the user launches a task without selecting an Agent
- **THEN** the composer shows no execution-configuration fields and submits the task with no execution hints

### Requirement: Launch feedback navigates into the running session

On an accepted launch the composer SHALL navigate the user into the created
session page, identified by the returned Session and Job identities, so the
user lands in the running session. On rejection the composer SHALL surface
actionable feedback — a conflict, a pending convergence, or an unavailable
server — and SHALL preserve the composed task so nothing the user entered is
lost.

#### Scenario: Success lands in the session

- **WHEN** the task-first launch succeeds
- **THEN** the user is navigated to the created AgentSession page
- **AND** the page shows the session with its first Turn processing the task

#### Scenario: Failure keeps the task

- **WHEN** the launch is rejected
- **THEN** the composer shows the rejection reason with its repair path
- **AND** the entered prompt and context references remain in the composer

### Requirement: Refinement after launch

After a task-first launch the created Agent SHALL remain refinable through the
existing Agent surfaces: the launch result and the session view SHALL expose a
path to the created Agent, where name, description, Instructions, and Skills
are editable with the existing definition editor. Refinements are ordinary
definition edits: they apply only to executions started afterwards and never
change the in-flight session.

#### Scenario: The user refines the created Agent

- **WHEN** the user opens the created Agent from the launch result and edits its name, Instructions, or Skills
- **THEN** the edits are saved through the definition editor
- **AND** they affect only AgentJobs started after the edit

### Requirement: The Agents empty state starts from a task

The Agents list empty state SHALL offer starting from a task as its primary
action: the action leads into the task-first composer rather than the
definition editor form. The definition-first editor SHALL remain available as
a secondary action for deliberate configuration.

#### Scenario: An empty Project starts from a task

- **WHEN** the Project has no Agents and the user uses the empty state's primary action
- **THEN** the user lands in the task-first composer with the task input first

#### Scenario: The definition editor remains reachable

- **WHEN** the user chooses deliberate configuration from the empty state
- **THEN** the definition-first Agent editor is available without going through the task composer
