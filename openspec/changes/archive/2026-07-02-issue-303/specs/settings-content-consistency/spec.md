## ADDED Requirements

### Requirement: System settings metadata reflects real server-provided values

The System settings section SHALL display metadata sourced from real server-provided values rather than hardcoded strings. The Log Path SHALL be read from `systemInfo.paths.logs`, consistent with the adjacent Paths card, and SHALL NOT be hardcoded to `~/.mohist/logs/`.

#### Scenario: Log Path is sourced from systemInfo.paths.logs

- **WHEN** the System settings section renders the Log Path
- **THEN** the displayed value SHALL equal `systemInfo.paths.logs` from the server-provided system info
- **AND** the value SHALL NOT be the hardcoded string `~/.mohist/logs/`

#### Scenario: Log Path stays consistent with the Paths card

- **WHEN** the System settings section renders both the Log Path row and the Paths card
- **THEN** the Log Path value SHALL match the logs entry shown in the Paths card

### Requirement: System settings relocates the orphan edit-config banner

The amber "Modify server-side config by editing config.jsonc..." banner SHALL NOT render as a detached orphan element. The guidance SHALL be relocated into its corresponding card or converted to an info tooltip so it visually belongs to the config it describes.

#### Scenario: Amber config banner is not an orphan element

- **WHEN** the System settings section renders
- **THEN** the edit-config guidance SHALL appear inside its related card or as an info tooltip
- **AND** the guidance SHALL NOT render as a standalone detached amber banner

### Requirement: Settings empty states carry an explicit next-step action

The "no project selected" empty state in Repositories, Label catalog, Templates, and Workflows SHALL present an explicit call-to-action to select or create a project, replacing the bare "No project selected" line. The empty-list states for Label catalog and Templates SHALL additionally present an inline next-step action (e.g. creating a new definition or template) rather than text alone.

#### Scenario: No-project empty state offers a select-or-create-project CTA

- **WHEN** a project-scoped Settings section (Repositories, Label catalog, Templates, or Workflows) renders without a selected project
- **THEN** the empty state SHALL present a call-to-action to select or create a project
- **AND** the empty state SHALL NOT render only the bare "No project selected" text

#### Scenario: Label catalog empty-list state offers an inline next step

- **WHEN** the Label catalog renders with a selected project but zero label definitions
- **THEN** the empty-list state SHALL present an inline action to create a new label definition
- **AND** the empty-list state SHALL NOT render only descriptive text

#### Scenario: Templates empty-list state offers an inline next step

- **WHEN** the Templates section renders with a selected project but zero templates
- **THEN** the empty-list state SHALL present an inline action to create a new template
- **AND** the empty-list state SHALL NOT render only descriptive text

### Requirement: Label catalog provides a search input consistent with Templates

The Label catalog section SHALL provide a search/filter input mirroring the Templates section's search behavior, allowing the user to filter the catalog by query.

#### Scenario: Label catalog renders a usable search input

- **WHEN** the Label catalog renders with a selected project
- **THEN** a search/filter input SHALL be rendered
- **AND** entering a query SHALL filter the displayed label definitions

### Requirement: Settings typography follows the baseline-ui pass

Settings section headings SHALL use `text-balance`, section descriptions SHALL use `text-pretty`, and System/Agent numeric and mono-spaced data rows SHALL use `tabular-nums`. This pass SHALL NOT introduce new motion or gradient effects.

#### Scenario: Section headings use text-balance

- **WHEN** a Settings section heading renders
- **THEN** the heading SHALL apply `text-balance`

#### Scenario: Section descriptions use text-pretty

- **WHEN** a Settings section description renders
- **THEN** the description SHALL apply `text-pretty`

#### Scenario: Numeric and mono data rows use tabular-nums

- **WHEN** a System or Agent settings numeric value or mono-spaced data row renders
- **THEN** the row SHALL apply `tabular-nums`

#### Scenario: Typography pass introduces no new motion or gradients

- **WHEN** the typography baseline is applied across Settings
- **THEN** no new animation or gradient effects SHALL be introduced
