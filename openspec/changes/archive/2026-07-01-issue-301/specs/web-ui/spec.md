## ADDED Requirements

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
