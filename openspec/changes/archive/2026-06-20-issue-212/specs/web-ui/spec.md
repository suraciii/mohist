## MODIFIED Requirements

### Requirement: Web UI supports issue model overrides

The Web UI SHALL let users configure an issue-level default model and optional per-stage model overrides from the issue workflow UI. Per-stage controls SHALL use real executable pipeline stages: `plan`, `build`, `check`, and `integrate`. Every model selector surface in the Web UI — the issue default and per-stage selectors, the project-level and per-stage default selectors, and the Agent editor model selector — SHALL offer an optional reasoning variant picker bound to the selected model. The variant picker SHALL present only the variants the selected model reports as supported via model discovery, SHALL be hidden when the selected model reports no variants, and SHALL refresh its presented set when the model changes. When the model changes or is cleared, a previously selected variant that the new model does not support SHALL be dropped.

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
- **AND** any variant bound to the cleared model is also cleared

#### Scenario: Stage lists match executable pipeline stages

- **WHEN** Settings or Issue Detail renders stage model override controls
- **THEN** the list includes `integrate`
- **AND** the list does not include `fix`

#### Scenario: Create issue with default model

- **WHEN** a user creates an issue from the Web UI and chooses a default model
- **THEN** the create request includes `model`
- **AND** the created issue stores that model override

#### Scenario: Variant picker shows only model-supported variants

- **WHEN** a user opens the variant picker for a selected model that reports one or more supported variants
- **THEN** the picker SHALL present only the variants reported by model discovery for that model
- **AND** SHALL NOT present variants the model does not report

#### Scenario: Variant picker hidden for models without variants

- **WHEN** the selected model reports no supported variants
- **THEN** the variant picker SHALL be hidden on every model selector surface
- **AND** the user SHALL NOT be able to enter a variant for that model

#### Scenario: Model change refreshes variant set and drops unsupported variant

- **WHEN** a user changes the selected model to a different model
- **THEN** the variant picker SHALL refresh to present the new model's reported variants
- **AND** a previously selected variant that the new model does not support SHALL be dropped from selection

#### Scenario: Stored variant shown when selector reopens

- **WHEN** a model selector reopens for a model that already has a stored variant
- **THEN** the variant picker SHALL show the previously stored variant as selected
- **AND** the stored variant SHALL be visible without re-running discovery beyond its cached results
