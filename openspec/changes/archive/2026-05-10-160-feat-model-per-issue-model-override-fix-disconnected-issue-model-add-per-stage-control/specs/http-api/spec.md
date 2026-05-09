## ADDED Requirements

### Requirement: Issue APIs expose model metadata

Issue create, update, list, and detail APIs SHALL accept and return issue-level model metadata where applicable. Model values SHALL use `provider/model` format, and invalid model metadata SHALL be rejected before persistence.

#### Scenario: Create issue with model metadata

- **WHEN** `POST /api/issues` is called with `model` and `stageModels`
- **THEN** the issue is created with those model overrides
- **AND** the response includes `model` and `stageModels`

#### Scenario: Update issue stage model overrides

- **WHEN** `PATCH /api/issues/:number` is called with `stageModels: { "plan": "anthropic/claude-opus-4-20250514" }`
- **THEN** the issue stage model overrides are replaced with the submitted map
- **AND** the response includes the updated `stageModels`

#### Scenario: Clear issue stage model overrides

- **WHEN** `PATCH /api/issues/:number` is called with `stageModels: null`
- **THEN** per-stage issue overrides are cleared
- **AND** the issue can fall back to global stage model configuration

#### Scenario: Reject invalid model metadata

- **WHEN** issue create or update receives a `model` or `stageModels` value that is not in `provider/model` format
- **THEN** the API returns HTTP 400
- **AND** the issue is not updated with the invalid model metadata
