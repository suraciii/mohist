## ADDED Requirements

### Requirement: API provides Agent CRUD endpoints

The HTTP API SHALL expose a project-scoped `/agents` endpoint group for Agent CRUD: `POST /agents`, `GET /agents`, `GET /agents/{id}`, `PATCH /agents/{id}`, and `DELETE /agents/{id}`. The project scope SHALL be taken from the current project context. The endpoint behavior SHALL align with the `IssueGrain` / `IIssueGrain` external shape and SHALL operate against `AgentGrain` instances keyed by `projectId|agentId`.

#### Scenario: Create Agent

- **WHEN** a client sends `POST /agents` with `{ name, instructions, description?, agentConfig?, skills?, maxConcurrentRuns? }`
- **AND** the current project context is set
- **THEN** the server SHALL create an Agent with `status` = `active` in the current project
- **AND** the response SHALL return HTTP 201 with the created Agent including its generated `id`, `createdAt`, and `updatedAt`

#### Scenario: Create requires project context

- **WHEN** a client sends `POST /agents`
- **AND** the server has no current project context
- **THEN** the server SHALL return HTTP 400
- **AND** the error SHALL indicate that no active project is set

#### Scenario: Create with duplicate name returns 409

- **WHEN** a client sends `POST /agents` with a `name` already used by another Agent in the same project (active or archived)
- **THEN** the server SHALL return HTTP 409
- **AND** the error SHALL identify the name conflict
- **AND** no Agent SHALL be created

### Requirement: API list Agents with status filtering

`GET /agents` SHALL return Agents in the current project context. By default it SHALL return only `status` = `active` Agents. The endpoint SHALL accept a `status` query parameter to filter by a single status value (e.g. `archived`), and an `all` query parameter (or equivalent) to include archived Agents alongside active ones.

#### Scenario: List defaults to active only

- **WHEN** a client requests `GET /agents`
- **THEN** the response SHALL include only Agents with `status` = `active`
- **AND** archived Agents SHALL NOT appear

#### Scenario: List with all includes archived

- **WHEN** a client requests `GET /agents?all=true`
- **THEN** the response SHALL include both active and archived Agents in the current project

#### Scenario: List filtered by single status

- **WHEN** a client requests `GET /agents?status=archived`
- **THEN** the response SHALL include only archived Agents
- **AND** active Agents SHALL NOT appear

#### Scenario: List is project-scoped

- **WHEN** a client requests `GET /agents`
- **THEN** the response SHALL include only Agents belonging to the current project context
- **AND** Agents from other projects SHALL NOT appear

### Requirement: API returns full Agent by id

`GET /agents/{id}` SHALL return the full Agent record, including `createdAt` and `updatedAt`, for any Agent in the current project context regardless of status. Reading an archived Agent by id SHALL succeed.

#### Scenario: Show returns full fields

- **WHEN** a client requests `GET /agents/{id}` for an existing Agent in the current project
- **THEN** the response SHALL include `id`, `projectId`, `name`, `description`, `instructions`, `agentConfig`, `skills`, `maxConcurrentRuns`, `status`, `createdAt`, and `updatedAt`

#### Scenario: Show archived Agent succeeds

- **WHEN** a client requests `GET /agents/{id}` for an archived Agent
- **THEN** the server SHALL return HTTP 200 with the full record
- **AND** the `status` field SHALL reflect `archived`

#### Scenario: Show unknown id returns 404

- **WHEN** a client requests `GET /agents/{id}` for an id that does not exist in the current project
- **THEN** the server SHALL return HTTP 404

#### Scenario: Cross-project read rejected

- **WHEN** a client requests `GET /agents/{id}` for an Agent belonging to a different project
- **THEN** the server SHALL return HTTP 404
- **AND** cross-project Agent data SHALL NOT leak

### Requirement: API updates Agent fields

`PATCH /agents/{id}` SHALL accept updates to `name`, `description`, `instructions`, `agentConfig`, `skills`, and `maxConcurrentRuns`. The endpoint SHALL refresh `updatedAt` on every successful update. The endpoint SHALL NOT allow modification of `createdAt`, `id`, or `projectId`. When `name` is changed, the endpoint SHALL apply the same project-scoped uniqueness check (including archived Agents) as create.

#### Scenario: Update mutable fields

- **WHEN** a client sends `PATCH /agents/{id}` with any subset of `description`, `instructions`, `agentConfig`, `skills`, or `maxConcurrentRuns`
- **THEN** the server SHALL apply the changes
- **AND** the server SHALL refresh `updatedAt`
- **AND** the response SHALL return the updated Agent

#### Scenario: Rename honors uniqueness

- **WHEN** a client sends `PATCH /agents/{id}` with a `name` already used by another Agent in the same project (active or archived)
- **THEN** the server SHALL return HTTP 409
- **AND** the Agent's existing `name` SHALL remain unchanged

#### Scenario: Immutable fields rejected

- **WHEN** a client sends `PATCH /agents/{id}` attempting to modify `createdAt`, `id`, or `projectId`
- **THEN** the server SHALL reject those fields
- **AND** the response SHALL NOT reflect changes to immutable fields

#### Scenario: Update unknown id returns 404

- **WHEN** a client sends `PATCH /agents/{id}` for an id that does not exist in the current project
- **THEN** the server SHALL return HTTP 404

### Requirement: API soft-deletes Agent on DELETE

`DELETE /agents/{id}` SHALL perform a soft delete by setting the Agent's `status` to `archived`. The endpoint SHALL NOT physically remove the Agent record. The endpoint SHALL refresh `updatedAt`. After archive, the `name` SHALL remain permanently occupied and SHALL NOT be reusable by a new Agent.

#### Scenario: Delete archives the Agent

- **WHEN** a client sends `DELETE /agents/{id}` for an active Agent
- **THEN** the server SHALL set `status` to `archived`
- **AND** the server SHALL refresh `updatedAt`
- **AND** the server SHALL NOT remove the record
- **AND** the response SHALL reflect the archived state

#### Scenario: Archived name cannot be reused via API

- **WHEN** an Agent has been archived via `DELETE /agents/{id}`
- **AND** a client sends `POST /agents` with the same `name`
- **THEN** the server SHALL return HTTP 409
- **AND** no new Agent SHALL be created

#### Scenario: Delete unknown id returns 404

- **WHEN** a client sends `DELETE /agents/{id}` for an id that does not exist in the current project
- **THEN** the server SHALL return HTTP 404

#### Scenario: Cross-project delete rejected

- **WHEN** a client sends `DELETE /agents/{id}` for an Agent belonging to a different project
- **THEN** the server SHALL return HTTP 404
- **AND** the Agent SHALL NOT be archived
