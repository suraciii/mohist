### Requirement: Server reverses archive via an UnarchiveAsync grain operation

The agent grain SHALL expose an `UnarchiveAsync` operation symmetric to `ArchiveAsync`. Unarchiving an archived agent SHALL set `Status` back to `active`, advance `UpdatedAt` to the current time, and persist the change.

#### Scenario: Unarchive transitions an archived agent back to active
- **WHEN** an archived agent is unarchived
- **THEN** the agent's `Status` SHALL become `active`
- **AND** the agent's `UpdatedAt` SHALL advance to the time of unarchive
- **AND** the persisted state SHALL reflect the active status

#### Scenario: Archive then unarchive round-trips to active
- **WHEN** an active agent is archived and then unarchived
- **THEN** the agent SHALL end in `active` status

### Requirement: Unarchive is exposed as a POST API route

The agent resource SHALL expose a `POST /{id}/unarchive` route that reverses archive, mirroring the Issue domain's unarchive precedent. The existing `DELETE /{id}` SHALL remain the archive verb, and `PATCH /{id}` SHALL remain unchanged and SHALL NOT accept a `status` field.

#### Scenario: POST unarchive reverses an archived agent
- **WHEN** `POST /api/projects/{projectRef}/agents/{id}/unarchive` is called for an archived agent
- **THEN** the response SHALL return the agent with a `status` of `active`

#### Scenario: Unarchive route does not alter archive or patch semantics
- **WHEN** the unarchive route is added
- **THEN** `DELETE /{id}` SHALL continue to perform archive
- **AND** `PATCH /{id}` SHALL continue to ignore any `status` field

### Requirement: Unarchiving an unknown agent returns not-found

Unarchiving an agent that does not exist SHALL return a not-found result, matching the archive path's contract.

#### Scenario: Unarchive of a non-existent agent
- **WHEN** unarchive is requested for an agent id that does not exist
- **THEN** the operation SHALL return a not-found result
- **AND** SHALL NOT create or mutate any agent

### Requirement: Unarchiving an already-active agent is well-defined

Unarchiving an agent whose status is already `active` SHALL produce a defined outcome (a no-op returning the active agent, or a well-defined error) rather than undefined or exceptional behavior.

#### Scenario: Unarchive of an already-active agent is handled deterministically
- **WHEN** unarchive is requested for an agent whose status is already `active`
- **THEN** the operation SHALL either no-op (returning the active agent) or return a well-defined error
- **AND** SHALL NOT throw an unhandled exception or corrupt state

### Requirement: Web client exposes an unarchive function and mutation

The web agent entity SHALL provide an `unarchiveAgent(projectId, id)` client function alongside `archiveAgent`, and a `useUnarchiveAgent` mutation alongside `useArchiveAgent`. The mutation's `onSuccess` SHALL invalidate the `['agents']` query cache so list and detail reflect the new state, and SHALL surface a success toast. Both the function and the mutation SHALL be exported from the agent entity barrel.

#### Scenario: unarchiveAgent client function calls the unarchive route
- **WHEN** `unarchiveAgent(projectId, id)` is invoked
- **THEN** it SHALL issue a `POST` request to the agent's `/unarchive` route

#### Scenario: useUnarchiveAgent invalidates the agents cache on success
- **WHEN** the `useUnarchiveAgent` mutation succeeds
- **THEN** the `['agents']` query cache SHALL be invalidated
- **AND** a success toast SHALL be shown

#### Scenario: Unarchive function and mutation are exported from the barrel
- **WHEN** a consumer imports from the agent entity barrel
- **THEN** `unarchiveAgent` and `useUnarchiveAgent` SHALL be available as exports

### Requirement: The detail page exposes an Unarchive affordance for archived agents

The agent detail page Actions card SHALL replace the static "This agent is archived and cannot be launched." notice with an actionable Unarchive/Restore control that returns the archived agent to active. After a successful unarchive, the agent SHALL re-appear in the Active list group and session launch SHALL be re-enabled.

#### Scenario: Archived agent detail page shows an Unarchive control
- **WHEN** the detail page renders for an archived agent
- **THEN** the Actions card SHALL present an Unarchive (Restore) control in place of the static archived notice

#### Scenario: Unarchive returns the agent to the Active group
- **WHEN** an archived agent is unarchived from the detail page
- **THEN** the agent SHALL re-appear in the Active group of the agent list
- **AND** SHALL no longer appear in the Archived group

#### Scenario: Session launch is re-enabled after unarchive
- **WHEN** an archived agent is unarchived
- **THEN** the detail-page "New Session" control SHALL become enabled for that agent
