# Settings UX Specification

### Requirement: Workflow profile cards show stage preview and concept explanation

The Workflows tab SHALL render each workflow profile card with a stage preview consisting of 4 chips (`plan -> build -> check -> integrate`) visible without navigating into the profile. Profile descriptions that exceed 2 lines SHALL be clamped with a "Read more" toggle that expands the full text. The Workflows tab SHALL display a top-level concept explanation (paragraph or tooltip) stating that workflow profiles define how issues move through stages.

#### Scenario: Stage chips are visible on the profile card
- **WHEN** a user opens the Settings > Workflows tab
- **THEN** each workflow profile card SHALL display 4 stage chips (`plan`, `build`, `check`, `integrate`)
- **AND** the chips SHALL be visible without clicking into the profile detail

#### Scenario: Long description collapses with Read more toggle
- **WHEN** a workflow profile description exceeds 2 lines
- **THEN** the card SHALL clamp the description to 2 lines
- **AND** SHALL display a "Read more" control that expands the full description on click

#### Scenario: Short description renders without toggle
- **WHEN** a workflow profile description fits within 2 lines
- **THEN** the description SHALL render in full
- **AND** no "Read more" control SHALL be displayed

#### Scenario: Concept explanation is shown at the top of the tab
- **WHEN** a user opens the Settings > Workflows tab
- **THEN** the tab SHALL display a 1-2 sentence explanation that workflow profiles define how issues move through stages
- **AND** the explanation SHALL be visible before the profile list

### Requirement: Repository empty state provides a prominent CTA with focus handoff

The Repositories tab SHALL render a prominent "Add your first repository" call-to-action button in the empty state (no repositories configured). Clicking the CTA SHALL automatically focus the repository Name input field. The inline "Add Repository" form SHALL NOT be rendered in the empty state (it is mutually exclusive with the CTA); the form SHALL only appear once at least one repository exists.

#### Scenario: Empty state shows prominent CTA
- **WHEN** a user opens the Settings > Repositories tab
- **AND** no repositories are configured for the project
- **THEN** the tab SHALL display a prominent "Add your first repository" button

#### Scenario: CTA click focuses the Name input
- **WHEN** a user clicks the "Add your first repository" CTA
- **THEN** the repository Name input field SHALL receive focus automatically

#### Scenario: Empty state does not duplicate the inline form
- **WHEN** the Repositories tab is in empty state (no repositories)
- **THEN** the inline "Add Repository" form SHALL NOT be rendered alongside the CTA

#### Scenario: Non-empty state renders the inline form
- **WHEN** at least one repository is configured
- **THEN** the inline "Add Repository" form SHALL be rendered
- **AND** the empty-state CTA SHALL NOT be displayed

### Requirement: First-visit onboarding banner on Coder Agent tab is dismissable and persistent

The Settings page SHALL display a dismissable info banner on the Coder Agent tab on first visit, with text guiding the user to start there (e.g., "Start here - select the coder agent model used for workflow tasks"). The first-visit and dismissal state SHALL be persisted in `localStorage`. Once dismissed, the banner SHALL NOT reappear on subsequent visits unless the persisted state is cleared.

#### Scenario: First visit shows the onboarding banner
- **WHEN** a user opens Settings and navigates to the Coder Agent tab for the first time
- **AND** no dismissal state is recorded in `localStorage`
- **THEN** the dismissable onboarding banner SHALL be displayed

#### Scenario: Dismissal persists across sessions
- **WHEN** a user dismisses the onboarding banner
- **THEN** the dismissal SHALL be persisted to `localStorage`
- **AND** on subsequent visits the banner SHALL NOT be displayed

#### Scenario: Clearing localStorage re-triggers the banner
- **WHEN** the onboarding dismissal state in `localStorage` is cleared
- **AND** the user reopens the Coder Agent tab
- **THEN** the onboarding banner SHALL be displayed again

### Requirement: Runtime fields expose business-level descriptions and corrected labels

The `AgentSettingsSection` SHALL display business-level descriptions for Runtime fields. `Max Concurrent` SHALL describe that the upper bound is constrained by runner capacity and that excess tasks queue. `Poll Interval` SHALL describe the tradeoff that shorter intervals are more realtime but consume more CPU/network. `Retry Budget` SHALL be relabeled to `Retry attempts` with the unit `times` (replacing the `grace periods` unit). Field descriptions SHALL be surfaced via tooltips on hover and focus.

#### Scenario: Max Concurrent has runner capacity description
- **WHEN** a user hovers or focuses on the `Max Concurrent` field
- **THEN** a tooltip SHALL appear explaining the upper bound is constrained by runner capacity and excess tasks queue

#### Scenario: Poll Interval has tradeoff description
- **WHEN** a user hovers or focuses on the `Poll Interval` field
- **THEN** a tooltip SHALL appear explaining that shorter intervals are more realtime but consume more CPU/network

#### Scenario: Retry Budget is relabeled to Retry attempts with times unit
- **WHEN** the `AgentSettingsSection` renders the retry field
- **THEN** the label SHALL read "Retry attempts"
- **AND** the unit SHALL read "times"
- **AND** the unit "grace periods" SHALL NOT be used

#### Scenario: Descriptions appear on hover and focus
- **WHEN** a user hovers over or keyboard-focuses any Runtime field with a description
- **THEN** the description tooltip SHALL become visible

### Requirement: Settings nav entry is gated on project context

The Settings navigation entry SHALL be hidden, or rendered with a "Select a project first" tooltip, when no project context is active. The normal Settings flow (with a selected project) SHALL be unaffected.

#### Scenario: No active project hides or disables the Settings entry
- **WHEN** no project context is active
- **THEN** the Settings nav entry SHALL be hidden
- **OR** the entry SHALL render with a "Select a project first" tooltip and SHALL NOT navigate into Settings

#### Scenario: Active project keeps Settings entry functional
- **WHEN** a project context is active
- **THEN** the Settings nav entry SHALL be visible and navigable as before
- **AND** no "Select a project first" tooltip SHALL be shown
