### Requirement: Web UI supports issue model overrides

The Web UI SHALL let users configure an issue-level default model and optional per-stage model overrides from the issue workflow UI. Per-stage controls SHALL use real executable pipeline stages: `plan`, `build`, `check`, and `integrate`. Model and model-variant selection SHALL be performed through a single unified variant-aware `ModelSelect` control rather than a model selector paired with a separate variant dropdown. The selected variant SHALL be captured alongside its model in issue `modelVariant` and `stageModelVariants`.

#### Scenario: Configure issue default model

- **WHEN** a user selects a model in the Issue Detail model selector
- **THEN** the UI updates the issue `model` through the issue API
- **AND** the selector shows that the issue-level override is active

#### Scenario: Configure issue stage model override

- **WHEN** a user expands advanced stage overrides on Issue Detail and selects a model for `build`
- **THEN** the UI updates `stageModels.build` through the issue API
- **AND** the issue detail refresh shows the selected build-stage override

#### Scenario: Select a variant in the same control as the model

- **WHEN** a user selects a variant chip on a model row in the default or per-stage `ModelSelect`
- **THEN** the UI SHALL capture both the model and the variant in a single selection action
- **AND** the issue default model variant (`modelVariant`) or stage variant (`stageModelVariants.<stage>`) SHALL be updated through the issue API
- **AND** the selector SHALL render the active variant as distinct from inactive variants

#### Scenario: Per-stage override uses the inline-chip compact pattern

- **WHEN** a user opens an advanced stage override row
- **THEN** the row SHALL render a single `size="compact"` variant-aware `ModelSelect`
- **AND** no standalone variant dropdown SHALL appear next to it

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

### Requirement: Web UI workflow profile selection is consistent

The Web UI SHALL read and write the issue's workflow profile selection as a single fact across the issue create form, the issue detail page, and the workflow-profile page. The value displayed on every surface SHALL be the issue's effective `workflowProfileId` resolved from the single source of truth. The create form selector, the issue detail display, and the workflow-profile page SHALL never present divergent profile values for the same issue.

#### Scenario: Create form selects workflow profile

- **WHEN** a user selects `mohist/pr` in the issue create form's workflow profile selector
- **THEN** the create request SHALL include `workflowProfileId: "mohist/pr"`
- **AND** the created issue's detail page SHALL display workflow profile `mohist/pr`

#### Scenario: Issue detail displays effective profile

- **WHEN** a user views an issue whose effective profile is `mohist/pr`
- **THEN** the issue detail page SHALL display `mohist/pr`
- **AND** the workflow-profile sub-page SHALL display the same `mohist/pr`

#### Scenario: Change profile from issue detail

- **WHEN** a user changes a backlog issue's workflow profile from `mohist/default` to `mohist/pr` on the issue detail page
- **THEN** the issue detail, workflow-profile page, and issue list SHALL all reflect `mohist/pr`

#### Scenario: Started issue blocks profile change in Web UI

- **WHEN** a user attempts to change the workflow profile of an issue that has an active workflow run
- **THEN** the Web UI SHALL surface a clear error that the issue has started
- **AND** the issue's workflow profile selection SHALL remain unchanged

#### Scenario: Create without selection inherits default

- **WHEN** a user creates an issue without choosing a workflow profile in the create form
- **THEN** the created issue SHALL have no issue-level selection
- **AND** the issue detail SHALL display the inherited default profile