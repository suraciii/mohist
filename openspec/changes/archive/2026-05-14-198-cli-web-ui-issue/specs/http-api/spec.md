## MODIFIED Requirements

### Requirement: REQ-API-198-001 Issue create accepts model with existing priority support

`POST /api/issues` SHALL accept optional `model` and `priority` fields in the same request body as title, body, and labels.

#### Scenario: Create issue with model and priority
- **WHEN** the server receives `POST /api/issues` with `{ title, body?, labels?, priority: "p1", model: "anthropic/claude-sonnet" }`
- **THEN** it creates the issue with both values persisted
- **AND** returns the created issue including `priority` and `model`

#### Scenario: Create issue with invalid model format
- **WHEN** the server receives `POST /api/issues` with `model: "invalid-model"`
- **THEN** it returns 400
- **AND** the error explains that `provider/model` format is required
