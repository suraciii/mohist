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

### Requirement: Create-issue success toast shows the new issue number

The Web UI create-issue flow SHALL confirm a successful creation with a success toast that displays the newly created issue's `number`. The create-issue mutation SHALL read `number` from the create API response's `Issue.number` field when building the toast message. The toast SHALL render a concrete number (e.g. `Issue #223 created`) and SHALL NOT render `undefined` or any other placeholder in place of the number. A failed create SHALL surface an error toast that does not reference an undefined number.

#### Scenario: Successful create shows correct issue number in toast

- **WHEN** a user submits the create-issue form and the create API returns an `Issue` with `number: 223`
- **THEN** the Web UI SHALL show a success toast containing the literal `Issue #223 created`
- **AND** the toast SHALL NOT display `undefined` in place of the number

#### Scenario: Create toast reads number from the create response

- **WHEN** the create-issue mutation's `onSuccess` handler runs with the API response
- **THEN** the toast message SHALL be built from the `Issue.number` field of the create response
- **AND** the handler SHALL NOT read the number from an undefined or mismatched response field

#### Scenario: Failed create shows error toast without an undefined number

- **WHEN** a create-issue request fails
- **THEN** the Web UI SHALL surface an error toast describing the failure
- **AND** the error toast SHALL NOT reference any issue number (and in particular SHALL NOT render `undefined`)

### Requirement: Settings Workflows tab renders real profile stages

Each workflow profile card on the Settings > Workflows tab SHALL render the profile's real `profile.stages` from profile detail. The cards SHALL NOT display a hardcoded stage set (e.g. `['plan','build','check','integrate']`) that is independent of the profile's actual stages.

#### Scenario: Profile card shows the profile's real stages

- **WHEN** a user opens Settings > Workflows for a project
- **THEN** each profile card SHALL display the stages from that profile's `profile.stages`
- **AND** no card SHALL display a hardcoded stage set that does not match the profile

#### Scenario: Cards with differing stages render distinctly

- **WHEN** a user views two profile cards whose `profile.stages` differ
- **THEN** each card SHALL display its own real stage set
- **AND** the two cards SHALL NOT show an identical hardcoded stage chip set

### Requirement: Settings Workflows tab exposes per-profile enable/disable

Each profile card on the Settings > Workflows tab SHALL expose a base-ui `Switch` primitive (with an `aria-label`) to toggle the per-project enable/disable state of that profile. The control SHALL reflect and write the project's disabled-profile blacklist. Attempting to disable the last remaining enabled profile SHALL be blocked inline with a clear consequence message.

#### Scenario: Switch toggles per-project enable/disable

- **WHEN** a user toggles the `Switch` on a profile card for the current project
- **THEN** the project's disabled-profile blacklist SHALL be updated accordingly
- **AND** the `Switch` SHALL expose an `aria-label` describing the profile it controls

#### Scenario: Switch is a base-ui primitive with aria-label

- **WHEN** a user views a profile card on Settings > Workflows
- **THEN** the enable/disable control SHALL be a base-ui `Switch` primitive
- **AND** the control SHALL NOT be a hand-rolled toggle
- **AND** the control SHALL carry an `aria-label`

#### Scenario: Last-enabled disable is blocked inline

- **WHEN** a user attempts to toggle off the only remaining enabled profile for the project
- **THEN** the action SHALL be blocked inline
- **AND** the profile SHALL remain enabled
- **AND** the UI SHALL show a clear consequence message explaining that at least one workflow must stay enabled

### Requirement: Project-default workflow control uses base-ui Select

The project-default workflow control on Settings > Workflows SHALL use the project's existing base-ui `Select` primitive and SHALL NOT use a native `<select>`. Disabled items in the dropdown SHALL have a clear visual distinction (greyed/disabled). When the current project default points at a profile that is on the project's disabled-profile blacklist, the control SHALL surface an amber warning.

#### Scenario: Default control is a base-ui Select

- **WHEN** a user views the project-default workflow control on Settings > Workflows
- **THEN** the control SHALL be the project's base-ui `Select` primitive
- **AND** the control SHALL NOT be a native `<select>`

#### Scenario: Disabled items are visually distinct in the dropdown

- **WHEN** a user opens the project-default dropdown for a project that has disabled profiles
- **THEN** the disabled items SHALL be rendered with a clear visual distinction (e.g. greyed or disabled)
- **AND** a disabled item SHALL be visually distinguishable from an enabled item

#### Scenario: Amber warning for a disabled default

- **WHEN** the current project default points at a profile that is on the project's disabled-profile blacklist
- **THEN** the control SHALL surface an amber warning

### Requirement: Workflow entries are discoverable in Settings Search

Workflow-related entries SHALL be registered in Settings Search so that the Workflows tab is discoverable. The Settings Search workflow descriptor registry SHALL NOT be empty.

#### Scenario: Settings Search returns workflow entries

- **WHEN** a user searches Settings for "workflow"
- **THEN** the search results SHALL include workflow-related entries that navigate to the Workflows tab

#### Scenario: Workflow descriptor registry is populated

- **WHEN** the Settings > Workflows tab is rendered
- **THEN** the workflow descriptor registry consumed by Settings Search SHALL contain at least one entry
- **AND** SHALL NOT be an empty array

### Requirement: Stage-progression surfaces render only real pipeline stages

Workflow stage-progression surfaces — the stage bar (`WorkflowView`), the session timeline (`SessionTimeline`), the kanban card (`IssueCard`), and the issue detail page (`IssueDetailPage`) — SHALL render only the real executable pipeline stages (`plan`, `build`, `check`, `integrate`). They SHALL NOT synthesize, derive, or append a terminal "Done" stage cell to the rendered stage list or stage order. The `WorkflowStage.Done` enum member MAY be retained for compatibility, but SHALL NOT be added to any rendered stage list or stage order, and SHALL NOT be used to override an issue's displayed stage. Terminal state SHALL continue to be expressed as "all real stages green + issue status pill", not as an additional stage.

#### Scenario: Stage bar renders only the real pipeline stages
- **WHEN** a user views an issue whose workflow has progressed (including one whose workflow run has completed)
- **THEN** the stage bar SHALL render exactly `plan`, `build`, `check`, and `integrate`
- **AND** SHALL NOT render a synthesized "Done" stage cell

#### Scenario: Session timeline stage order excludes Done
- **WHEN** the session timeline derives its `stageOrder`
- **THEN** `stageOrder` SHALL contain only `plan`, `build`, `check`, and `integrate`
- **AND** SHALL NOT contain `done`

#### Scenario: Kanban card does not override stage to Done
- **WHEN** a kanban card renders the current stage for an issue whose status is `Done`
- **THEN** the card SHALL NOT override the displayed stage to `WorkflowStage.Done`
- **AND** SHALL derive the stage from the real pipeline stages only

#### Scenario: Issue detail omits a Done stage label
- **WHEN** the issue detail page renders its stage label map
- **THEN** it SHALL NOT include a `WorkflowStage.Done` label entry
- **AND** SHALL label only the real pipeline stages
