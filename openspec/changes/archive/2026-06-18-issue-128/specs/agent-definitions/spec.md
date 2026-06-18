## ADDED Requirements

### Requirement: Agent Domain Model

The system SHALL model an Agent as a project-scoped, named, reusable role definition. An Agent SHALL persist the following fields: `id` (system-assigned), `projectId`, `name`, `description`, `instructions`, `agentConfig`, `skills`, `maxConcurrentRuns`, `status`, `createdAt`, and `updatedAt`. An Agent SHALL NOT carry workflow stage, run state, worktree, branch, or execution-tracking fields. Agents SHALL be separate from issues and SHALL NOT hold a foreign key to the `Issues` table; references to an Agent are by id or name, not by strong binding.

#### Scenario: Create active Agent

- **WHEN** a user creates an Agent with a name and instructions
- **THEN** the system SHALL persist the Agent with `status` = `active`
- **AND** the system SHALL assign a system-generated `id`, `createdAt`, and `updatedAt`
- **AND** the Agent SHALL carry no workflow stage, run state, worktree, or branch fields

#### Scenario: Agent is not executable work

- **WHEN** the system stores or reads an Agent
- **THEN** the Agent SHALL NOT contain execution-tracking, run-instance, or issue-binding state
- **AND** running an Agent by name is outside the scope of this capability

### Requirement: Agent name uniqueness within project including archived

The system SHALL enforce that an Agent's `name` is unique within its project scope. Uniqueness SHALL include archived Agents: once an Agent's `name` has been used in a project, no other Agent in the same project (active or archived) MAY take that `name`. This SHALL prevent historical-reference ambiguity.

#### Scenario: Duplicate name rejected on create

- **WHEN** a user creates an Agent with a `name` already used by another Agent in the same project
- **THEN** the system SHALL reject the create operation with a name-conflict error
- **AND** no new Agent SHALL be persisted

#### Scenario: Archived name remains occupied

- **WHEN** an Agent has been archived (`status` = `archived`)
- **AND** a user attempts to create a new Agent with the same `name` in the same project
- **THEN** the system SHALL reject the create operation with a name-conflict error
- **AND** the archived Agent SHALL remain archived

#### Scenario: Name uniqueness is project-scoped

- **WHEN** two different projects each contain an Agent named `reviewer`
- **THEN** the system SHALL treat them as distinct
- **AND** neither SHALL conflict with the other

#### Scenario: Rename honors uniqueness

- **WHEN** a user updates an Agent's `name` to a value already used by another Agent in the same project
- **THEN** the system SHALL reject the update with a name-conflict error
- **AND** the Agent's existing `name` SHALL remain unchanged

### Requirement: Agent soft delete via archive

The system SHALL provide soft delete for Agents by setting `status` to `archived`. The system SHALL NOT provide hard delete of an Agent record. An archived Agent's `name` SHALL remain permanently occupied (see name uniqueness requirement).

#### Scenario: Delete archives the Agent

- **WHEN** a user deletes an Agent
- **THEN** the system SHALL set the Agent's `status` to `archived`
- **AND** the system SHALL NOT remove the Agent record from persistence
- **AND** the system SHALL refresh `updatedAt`

#### Scenario: Hard delete is not available

- **WHEN** any operation attempts to physically remove an Agent record
- **THEN** the system SHALL NOT perform a hard delete
- **AND** only the archive transition SHALL be available as the deletion semantic

#### Scenario: Archived Agent remains readable

- **WHEN** an Agent has been archived
- **THEN** the system SHALL still return the Agent for direct read by id
- **AND** the system SHALL exclude the Agent from default active-only listings

### Requirement: Agent instructions stored as free text

The system SHALL store `instructions` as free-form text with no schema. The system SHALL store the raw text verbatim and SHALL NOT perform template rendering, variable substitution, or transformation on stored instructions. Any rendering or substitution SHALL be the responsibility of the consuming layer, not this capability.

#### Scenario: Instructions persisted verbatim

- **WHEN** a user creates or updates an Agent with `instructions` containing literal text
- **THEN** the system SHALL persist the exact text without modification
- **AND** a subsequent read SHALL return byte-identical instructions

#### Scenario: No template rendering on store

- **WHEN** `instructions` contain mustache-style, `${var}`, or other template syntax
- **THEN** the system SHALL store the syntax as literal text
- **AND** SHALL NOT attempt to resolve or render it

### Requirement: Agent agentConfig reuses BuildAgentConfig shape

The system SHALL persist `agentConfig` as the JSON shape produced by `MohistIssueWorkflowProfileBase.BuildAgentConfig` (a `Dictionary<string,object?>` containing fields such as `type`, `model`, and opencode settings). The system SHALL NOT use the legacy `IssueInfo.AgentConfig` attribute for this value. The `agentConfig` value SHALL be treated as opaque metadata by this capability; semantic validation beyond well-formed JSON SHALL be the responsibility of the consuming layer.

#### Scenario: agentConfig persisted as config dictionary

- **WHEN** a user creates or updates an Agent with an `agentConfig` value
- **THEN** the system SHALL persist it as the `BuildAgentConfig` JSON shape
- **AND** the system SHALL NOT write the value to `IssueInfo.AgentConfig`

#### Scenario: Legacy IssueInfo.AgentConfig remains untouched

- **WHEN** any Agent create, update, or delete operation runs
- **THEN** the system SHALL NOT read or write the `IssueInfo.AgentConfig` attribute
- **AND** `IssueVariableBuilder` and `BuildAgentConfig` SHALL remain unchanged

### Requirement: Agent skills and maxConcurrentRuns are declarative metadata only

The system SHALL persist `skills` and `maxConcurrentRuns` as declarative metadata. In v1, the runner SHALL NOT consume `skills` to isolate filesystem-ambient skill discovery, and `maxConcurrentRuns` SHALL NOT be enforced by this capability; enforcement is the responsibility of the execution layer (#126). This capability SHALL only persist and return these values.

#### Scenario: skills persisted as metadata

- **WHEN** a user creates or updates an Agent with a `skills` value
- **THEN** the system SHALL persist the value verbatim
- **AND** the runner SHALL NOT alter filesystem-ambient skill discovery based on it

#### Scenario: maxConcurrentRuns persisted as soft cap

- **WHEN** a user creates or updates an Agent with a `maxConcurrentRuns` value
- **THEN** the system SHALL persist the value
- **AND** this capability SHALL NOT reject concurrent work based on it
- **AND** enforcement SHALL be deferred to the execution layer

### Requirement: AgentGrain persistence reuses IssueGrain pattern

The system SHALL persist Agents via an `AgentGrain : Grain, IAgentGrain` that reuses the `IssueGrain` persistence pattern: an `IStateStore<Agent>`, an `OnActivateAsync` load of the stored state, and a string primary key from `GetPrimaryKeyString()`. The grain key SHALL encode the project scope (e.g. `projectId|agentId`) to guarantee cross-project isolation. A grain activation SHALL load the Agent state on activation and SHALL serve create/read/update/archive operations through grain methods.

#### Scenario: Grain key encodes project scope

- **WHEN** the system activates an `AgentGrain`
- **THEN** the grain key SHALL encode both the `projectId` and the `agentId`
- **AND** two Agents with the same `agentId` in different projects SHALL activate as distinct grains

#### Scenario: Grain loads state on activation

- **WHEN** an `AgentGrain` activates
- **THEN** the grain SHALL load its persisted Agent state via `IStateStore<Agent>`
- **AND** subsequent grain method calls SHALL operate on the loaded state

#### Scenario: Grain operations cover the CRUD lifecycle

- **WHEN** grain tests exercise create, show, update, archive, and name-uniqueness paths
- **THEN** the `AgentGrain` SHALL satisfy each operation through grain methods
- **AND** name-uniqueness checks SHALL be enforced inside the grain or its persistence layer

### Requirement: Agents table and EF migration

The system SHALL add a new `Agents` table via an EF Core migration. The migration SHALL provide both a forward-apply script and a clean-rollback script. The `Agents` table SHALL NOT define a foreign key to the `Issues` table. Persistence SHALL support the project-scoped uniqueness constraint on `name` (including archived rows) required by the name-uniqueness requirement.

#### Scenario: Forward migration creates Agents table

- **WHEN** the EF migration is applied forward
- **THEN** the `Agents` table SHALL be created with columns for every Agent field
- **AND** the table SHALL enforce project-scoped uniqueness on `name` across active and archived rows

#### Scenario: Rollback migration cleanly removes Agents table

- **WHEN** the EF migration is rolled back
- **THEN** the `Agents` table SHALL be removed cleanly
- **AND** the rollback SHALL leave the `Issues` table and all other existing schema unchanged

#### Scenario: No foreign key to Issues

- **WHEN** the `Agents` table schema is inspected
- **THEN** it SHALL NOT define any foreign key referencing the `Issues` table
- **AND** Agent references SHALL be by id or name only
