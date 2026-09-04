### Requirement: The Agent definition carries the full task profile

The Agent definition SHALL carry purpose, description, instructions, permissions, collaborators, and concurrency intent as first-class, persisted definition fields. Purpose and description are free-text task language owned by the definition; collaborators are the allowed subagent Agent references; concurrency intent is the maximum number of concurrent runs. The Server MUST accept every task-profile field on Agent create and update, persist it with the definition, and return it on every definition read.

#### Scenario: Creating an Agent with the full task profile

- **WHEN** a caller creates an Agent supplying purpose, description, instructions, a permission declaration, collaborators, and concurrency intent
- **THEN** the Server SHALL persist all of the task-profile fields with the new definition
- **AND** the create response SHALL return every task-profile field

#### Scenario: Updating one task field leaves the others intact

- **WHEN** a caller updates only the purpose of an existing Agent
- **THEN** the Server SHALL persist the new purpose
- **AND** the description, instructions, permissions, collaborators, and concurrency intent MUST remain unchanged

#### Scenario: Every definition read exposes the task profile

- **WHEN** any surface reads an Agent list entry or Agent detail after this change
- **THEN** the returned definition SHALL include purpose, description, instructions, permissions, collaborators, and concurrency intent

### Requirement: Web and CLI authoring parity

Every task-profile field SHALL be editable in both the Web Agent profile editor and `mo agent create` / `mo agent edit`. A definition field MUST NOT be settable in only one surface. Setting or clearing a field through one surface MUST produce the same persisted definition as setting or clearing it through the other.

#### Scenario: The Web editor edits previously CLI-only fields

- **WHEN** a user edits an Agent in the Web profile editor and sets the description, collaborators, and concurrency intent
- **THEN** the save SHALL persist those fields
- **AND** a subsequent `mo agent view` of the same Agent SHALL show the same values

#### Scenario: The CLI edits every field the Web editor exposes

- **WHEN** a user runs `mo agent edit` to set the purpose, description, permissions, collaborators, or concurrency intent
- **THEN** the update SHALL persist those fields
- **AND** the Web Agent detail SHALL show the same values

#### Scenario: Clearing an optional task field

- **WHEN** a user clears an optional task-profile field in either surface
- **THEN** the persisted definition SHALL record the field as cleared
- **AND** the other surface MUST NOT resurrect the previous value on its next read

### Requirement: Permission declaration uses a closed vocabulary

The Agent permission declaration SHALL state what the Agent may operate on, using permission terms from the closed vocabulary defined by the design. The Server MUST validate the declaration at the Agent-definition write boundary: a declaration containing a term outside the vocabulary SHALL be rejected with an actionable validation error that names the offending term and the accepted vocabulary, and no definition change SHALL be persisted. A valid declaration SHALL be persisted with the definition and included in every definition projection. The permission declaration is definition state; it is not a launch-time input.

#### Scenario: An unknown permission term is rejected

- **WHEN** a caller creates or updates an Agent whose permission declaration contains a term outside the vocabulary
- **THEN** the Server SHALL reject the write with a validation error naming the offending term and the accepted vocabulary
- **AND** the previously persisted definition MUST remain unchanged

#### Scenario: A valid declaration persists and projects

- **WHEN** a caller saves a declaration composed only of vocabulary terms
- **THEN** the Server SHALL persist the declaration with the definition
- **AND** Agent detail projections in the Web UI and the CLI SHALL render the declared permission scope

#### Scenario: Omitting the declaration remains valid

- **WHEN** a caller creates or updates an Agent without supplying a permission declaration
- **THEN** the Server SHALL accept the write and record the absence of a declaration on the definition

### Requirement: Task-first authoring structure

The create and edit surfaces SHALL be organized around the task: purpose, description, instructions, permissions, collaborators, and concurrency intent are presented as the primary authoring fields, and the execution backend and model are presented as a purpose-guided secondary choice. A raw `provider/model` string MUST NOT be the leading way the authoring surface asks a user to define an Agent.

#### Scenario: The editor leads with task language

- **WHEN** a user opens the Web Agent profile editor or the `mo agent create` help after this change
- **THEN** the task-profile fields SHALL be presented as the primary structure of the surface
- **AND** runtime and model SHALL be presented as a secondary, purpose-guided selection rather than the first required choice

### Requirement: Saving states effective-time semantics

When an Agent definition edit is saved, the saving surface SHALL state the effective-time semantics: the edits apply to Jobs launched after the save, and Jobs already running keep the launch facts recorded at their own launch. The persisted behavior MUST match the statement — a definition save SHALL NOT rewrite the launch facts, execution configuration, instructions, or permission scope of a Job that is already running or was launched earlier.

#### Scenario: The surface states the effective scope when saving

- **WHEN** a user saves an Agent definition edit in the Web editor or through `mo agent edit`
- **THEN** the surface SHALL state that the saved definition applies to Jobs launched afterwards and that already-running Jobs keep their launch facts unchanged

#### Scenario: A running Job keeps its launch facts

- **WHEN** an Agent has a running Job and the Agent's definition is edited and saved
- **THEN** the running Job SHALL continue executing with the definition facts recorded at its launch
- **AND** the next launch of the same Agent SHALL use the newly saved definition
