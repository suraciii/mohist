## ADDED Requirements

### Requirement: Epic API Endpoints

Server SHALL expose REST endpoints for Epic creation, listing, detail, membership management, and lifecycle actions.

#### Scenario: Create Epic through API

- **WHEN** a client sends `POST /api/epics` with title, description, and priority
- **THEN** the server creates an active Epic
- **AND** invalid input returns a structured validation error

#### Scenario: List Epics through API

- **WHEN** a client sends `GET /api/epics`
- **THEN** the response includes Epic status, priority, progress, and next issue data for each Epic

#### Scenario: Show Epic through API

- **WHEN** a client sends `GET /api/epics/:id`
- **THEN** the response includes full description, status, priority, linked issues, projected progress, and next issue data

#### Scenario: Add issue through API

- **WHEN** a client sends `POST /api/epics/:id/issues` with an issue id
- **THEN** the server links the issue to the Epic
- **AND** duplicate primary membership returns a structured error that identifies the existing Epic

#### Scenario: Remove issue through API

- **WHEN** a client sends `DELETE /api/epics/:id/issues/:issueId`
- **THEN** the server removes only that membership

#### Scenario: Mark Epic done through API

- **WHEN** a client sends `POST /api/epics/:id/done`
- **THEN** the server changes only the Epic status to `done`

#### Scenario: Close Epic through API

- **WHEN** a client sends `POST /api/epics/:id/close`
- **THEN** the server changes only the Epic status to `closed`

### Requirement: Issue Detail Primary Epic Data

Server SHALL expose a linked issue's primary Epic summary for Issue Detail without adding Epics to issue workflow lists.

#### Scenario: Linked issue detail includes primary Epic

- **WHEN** a client requests detail for an issue linked to an Epic
- **THEN** the response includes the primary Epic id, title, status, and priority

#### Scenario: Unlinked issue detail has no primary Epic

- **WHEN** a client requests detail for an issue without Epic membership
- **THEN** the response clearly indicates no primary Epic is linked

#### Scenario: Board lanes remain issue-only

- **WHEN** a client requests Board lane data or issue workflow lists
- **THEN** Epics are not returned as workflow items
