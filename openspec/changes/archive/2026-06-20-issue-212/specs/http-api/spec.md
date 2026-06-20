## MODIFIED Requirements

### Requirement: Issue APIs expose model metadata

Issue create, update, list, and detail APIs SHALL accept and return issue-level model metadata where applicable. Model values SHALL use `provider/model` format, and invalid model metadata SHALL be rejected before persistence. A reasoning variant MAY accompany a model value wherever model metadata is accepted: an issue-level default variant alongside `model`, and per-stage variants alongside `stageModels`. A variant value that is null or absent SHALL mean no variant override. Clearing a model (setting it to null) SHALL also clear its bound variant.

#### Scenario: Create issue with model metadata

- **WHEN** `POST /api/issues` is called with `model` and `stageModels`
- **THEN** the issue is created with those model overrides
- **AND** the response includes `model` and `stageModels`
- **AND** an accompanying variant value, when provided, is stored and returned alongside its model

#### Scenario: Update issue stage model overrides

- **WHEN** `PATCH /api/issues/:number` is called with `stageModels: { "plan": "anthropic/claude-opus-4-20250514" }`
- **THEN** the issue stage model overrides are replaced with the submitted map
- **AND** the response includes the updated `stageModels`
- **AND** an accompanying per-stage variant, when provided, is stored and returned alongside that stage's model

#### Scenario: Clear issue model overrides

- **WHEN** `PATCH /api/issues/:number` is called with `stageModels: null`
- **THEN** per-stage issue overrides are cleared
- **AND** the issue can fall back to global stage model configuration
- **AND** any variant bound to a cleared model is also cleared

#### Scenario: Reject invalid model metadata

- **WHEN** issue create or update receives a `model` or `stageModels` value that is not in `provider/model` format
- **THEN** the API returns HTTP 400
- **AND** the issue is not updated with the invalid model metadata

#### Scenario: Variant round-trips through create, update, and show

- **WHEN** an issue is created or updated with a model and an accompanying variant
- **THEN** subsequent list and detail responses SHALL return the variant alongside its model
- **AND** re-opening the selector SHALL show the previously stored variant

## ADDED Requirements

### Requirement: Opencode models endpoint exposes per-model variants

`GET /api/projects/{projectId}/opencode/models` SHALL return each selectable coder model together with that model's supported reasoning variant set as reported by runner model discovery. A model with no reported variants SHALL be associated with an empty variant set. The endpoint SHALL remain strictly additive and backward compatible: the existing `models` list SHALL keep the same shape the client consumed before this capability, and a client that ignores variant data SHALL continue to receive the model identifiers it consumes today.

#### Scenario: Endpoint returns variant set per model

- **WHEN** a client requests `GET /api/projects/{projectId}/opencode/models`
- **THEN** the response SHALL associate each model with its supported reasoning variant set as reported by discovery
- **AND** the variant set SHALL reflect the currently registered runner discovery results

#### Scenario: Models without variants return an empty set

- **WHEN** a registered model reports no supported variants
- **THEN** the endpoint SHALL associate that model with an empty variant set (absent from the variant map or mapped to an empty array)
- **AND** SHALL NOT omit the model from the `models` list

#### Scenario: Backward compatible with variant-agnostic clients

- **WHEN** a client consumes the endpoint while ignoring variant data
- **THEN** the model identifiers SHALL remain available in the same shape the client consumed before this capability
- **AND** the presence of variant fields SHALL NOT break that client
