## MODIFIED Requirements

### Requirement: Issue model metadata storage

The local issue store SHALL persist issue-level model metadata as nullable fields: `model` for the issue default and `stageModels` for per-stage overrides. Missing or null values SHALL mean no issue-level override and SHALL NOT materialize inherited global defaults into the issue row. A reasoning variant MAY be persisted alongside its model: an issue-level default variant alongside `model` and per-stage variants alongside `stageModels`. Clearing a model value (null) SHALL clear its bound variant. Variant persistence SHALL NOT introduce a schema migration; variants SHALL ride on the existing model metadata JSON.

#### Scenario: Store per-issue stage model overrides

- **WHEN** an issue is created or updated with `stageModels: { "build": "anthropic/claude-sonnet-4-20250514" }`
- **THEN** subsequent issue reads return `stageModels.build = "anthropic/claude-sonnet-4-20250514"`
- **AND** the persisted value is stored in the issue row as nullable JSON text
- **AND** an accompanying per-stage variant, when provided, is persisted alongside that stage's model in the same JSON

#### Scenario: Clear per-issue stage model overrides

- **WHEN** an issue is updated with `stageModels: null` or an empty override map
- **THEN** subsequent issue reads return no per-stage issue overrides
- **AND** model resolution can fall back to global stage models
- **AND** any variant bound to a cleared stage model is also cleared

#### Scenario: Malformed stored stage model JSON

- **WHEN** an existing issue row contains malformed `stage_models` JSON
- **THEN** issue reads succeed
- **AND** the issue is returned without per-stage issue overrides

#### Scenario: Stored variant round-trips on read

- **WHEN** an issue is created or updated with a model and an accompanying variant
- **THEN** subsequent issue reads return the variant alongside its model
- **AND** the variant is recovered from the same nullable JSON text as its model
