## MODIFIED Requirements

### Requirement: Web UI supports issue model overrides

The Web UI SHALL let users configure an issue-level default model and optional per-stage model overrides from the issue workflow UI. Per-stage controls SHALL use real executable pipeline stages: `explore`, `plan`, `build`, `check`, and `integrate`.

#### Scenario: Configure issue default model

- **WHEN** a user selects a model in the Issue Detail model selector
- **THEN** the UI updates the issue `model` through the issue API
- **AND** the selector shows that the issue-level override is active

#### Scenario: Configure issue stage model override

- **WHEN** a user expands advanced stage overrides on Issue Detail and selects a model for `build`
- **THEN** the UI updates `stageModels.build` through the issue API
- **AND** the issue detail refresh shows the selected build-stage override

#### Scenario: Clear issue model overrides

- **WHEN** a user clears the issue default model or a stage-specific override
- **THEN** the UI sends `null` or an override map without that stage as appropriate
- **AND** the issue falls back to lower-priority model configuration

#### Scenario: Stage lists match executable pipeline stages

- **WHEN** Settings or Issue Detail renders stage model override controls
- **THEN** the list includes `integrate`
- **AND** the list does not include `fix`

#### Scenario: Create issue with default model

- **WHEN** a user creates an issue from the Web UI and chooses a default model
- **THEN** the create request includes `model`
- **AND** the created issue stores that model override
