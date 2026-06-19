## MODIFIED Requirements

### Requirement: REQ-API-198-001 Issue create accepts model with existing priority support

`POST /api/issues` SHALL accept optional `model` and `priority` fields in the same request body as title, body, and labels. The `labels` field SHALL be a key-value map (JSON object) governed by the `issue-labels` capability, where each key maps to at most one value. An invalid label key or an empty label value SHALL be rejected with HTTP 400 and a clear error, and SHALL NOT persist the issue.

#### Scenario: Create issue with model and priority
- **WHEN** the server receives `POST /api/issues` with `{ title, body?, labels: { "stream": "frontend" }, priority: "p1", model: "anthropic/claude-sonnet" }`
- **THEN** it creates the issue with both values persisted
- **AND** returns the created issue including `priority`, `model`, and the `labels` key-value map

#### Scenario: Create issue with invalid model format
- **WHEN** the server receives `POST /api/issues` with `model: "invalid-model"`
- **THEN** it returns 400
- **AND** the error explains that `provider/model` format is required

#### Scenario: Create issue with invalid label key is rejected
- **WHEN** the server receives `POST /api/issues` with `{ title, labels: { "Stream": "frontend" } }` (uppercase key)
- **THEN** it returns 400
- **AND** the error explains the valid label key format

#### Scenario: Create issue with empty label value is rejected
- **WHEN** the server receives `POST /api/issues` with `{ title, labels: { "stream": "" } }`
- **THEN** it returns 400
- **AND** the error explains that label values must be non-empty

## ADDED Requirements

### Requirement: Issue label API uses key-value model

The HTTP API SHALL treat Issue labels as key-value pairs governed by the `issue-labels` capability. `POST /api/issues` and `PATCH /api/issues/:id` SHALL accept a `labels` field as a key-value map (full replacement semantics on update). `GET /api/labels` SHALL return the distinct label keys used across the current project's issues, so surfaces can present the available classification dimensions.

#### Scenario: Update issue labels via full replacement
- **WHEN** the server receives `PATCH /api/issues/:id` with `{ labels: { "module": "auth" } }`
- **THEN** the issue's label map becomes exactly `{ "module": "auth" }`
- **AND** any previously present keys are removed

#### Scenario: Label set by key persists a single value
- **WHEN** the server receives `PATCH /api/issues/:id` with `{ labels: { "stream": "backend" } }` for an issue whose `stream` was `frontend`
- **THEN** the issue's label map contains `{ "stream": "backend" }`
- **AND** the prior value `frontend` is no longer present

#### Scenario: GET labels returns distinct keys
- **WHEN** the client requests `GET /api/labels` for a project whose issues use keys `stream`, `module`, and `stream`
- **THEN** the response contains the distinct keys `stream` and `module`
- **AND** the keys conform to the label key validation rule
