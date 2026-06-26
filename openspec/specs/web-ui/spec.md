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

- **WHEN** a user changes a backlog issue's workflow profile from `mohist/local` to `mohist/pr` on the issue detail page
- **THEN** the issue detail, workflow-profile page, and issue list SHALL all reflect `mohist/pr`

#### Scenario: Started issue blocks profile change in Web UI

- **WHEN** a user attempts to change the workflow profile of an issue that has an active workflow run
- **THEN** the Web UI SHALL surface a clear error that the issue has started
- **AND** the issue's workflow profile selection SHALL remain unchanged

#### Scenario: Create without selection inherits default

- **WHEN** a user creates an issue without choosing a workflow profile in the create form
- **THEN** the created issue SHALL have no issue-level selection
- **AND** the issue detail SHALL display the inherited default profile

### Requirement: Archived issue detail page renders workflow execution history

The Web UI issue detail page SHALL render the full workflow execution history for an archived issue from its preserved workflow run reference. Archiving SHALL NOT cause the detail page to hide, omit, or fail to load the workflow timeline, artifacts, events, feedback, commits, diffs, or execution context. The archived detail page SHALL use the same rendering path as a non-archived issue detail page, differing only in visibility/list placement, not in history access.

#### Scenario: Archived issue detail shows the workflow timeline

- **WHEN** a user opens the detail page of a `Done` issue that was archived after completing workflow run `wr_1`
- **THEN** the page SHALL render the workflow timeline for `wr_1`
- **AND** the page SHALL display the `archivedAt` state without removing execution history

#### Scenario: Archived issue detail shows artifacts and feedback

- **WHEN** a user opens the detail page of an archived issue
- **THEN** the page SHALL render the artifacts, events, and feedback produced by the preserved workflow run
- **AND** no history section that renders for a non-archived `Done` issue SHALL be hidden for the archived issue

#### Scenario: Archived detail does not show an active workflow control surface

- **WHEN** a user views an archived issue detail page whose `workflowRunId` is preserved
- **THEN** the page SHALL NOT present active-workflow controls (start/stop/retry) as if the workflow were running
- **AND** any workflow status indicator SHALL reflect the archived/`Done` state
