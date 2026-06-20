## MODIFIED Requirements

### Requirement: Epic API Endpoints

Server SHALL expose REST endpoints for Epic creation, listing, detail, membership management, and lifecycle actions including pause and resume. Lifecycle endpoints SHALL enforce the Epic transition rules: pause and resume move an Epic between `active` and `paused`; close is allowed from `active` or `paused`; mark done is rejected when the Epic is `paused`. The detail and list responses SHALL include the optional pause reason when present.

#### Scenario: Create Epic through API

- **WHEN** a client sends `POST /api/epics` with title, description, and priority
- **THEN** the server creates an active Epic
- **AND** invalid input returns a structured validation error

#### Scenario: List Epics through API

- **WHEN** a client sends `GET /api/epics`
- **THEN** the response includes Epic status, priority, progress, next issue data, and pause reason (when present) for each Epic

#### Scenario: Show Epic through API

- **WHEN** a client sends `GET /api/epics/:id`
- **THEN** the response includes full description, status, priority, linked issues, projected progress, next issue data, and pause reason (when present)

#### Scenario: Add issue through API

- **WHEN** a client sends `POST /api/epics/:id/issues` with an issue id
- **THEN** the server links the issue to the Epic
- **AND** duplicate primary membership returns a structured error that identifies the existing Epic

#### Scenario: Remove issue through API

- **WHEN** a client sends `DELETE /api/epics/:id/issues/:issueId`
- **THEN** the server removes only that membership

#### Scenario: Pause Epic through API

- **WHEN** a client sends `POST /api/epics/:id/pause` for an `active` Epic, optionally with a pause reason
- **THEN** the server changes only the Epic status to `paused` and persists the pause reason
- **AND** linked issues are not modified or unbound

#### Scenario: Resume Epic through API

- **WHEN** a client sends `POST /api/epics/:id/resume` for a `paused` Epic
- **THEN** the server changes only the Epic status to `active`
- **AND** the persisted pause reason is cleared
- **AND** linked issues are not modified

#### Scenario: Mark Epic done through API

- **WHEN** a client sends `POST /api/epics/:id/done` for an `active` Epic
- **THEN** the server changes only the Epic status to `done`

#### Scenario: Mark done rejected for paused Epic through API

- **WHEN** a client sends `POST /api/epics/:id/done` for a `paused` Epic
- **THEN** the server rejects the request with a structured error
- **AND** the error indicates the Epic MUST be resumed first

#### Scenario: Close Epic through API

- **WHEN** a client sends `POST /api/epics/:id/close` for an `active` or `paused` Epic
- **THEN** the server changes only the Epic status to `closed`
