## MODIFIED Requirements

### Requirement: Model Select Popover renders and is interactive

Settings AI page SHALL provide a `ModelSelect` component that opens a popover panel when clicked, allowing users to search and select a model. The `Popover.Panel` SHALL render directly without a `Transition` wrapper, compatible with `@headlessui/react` v2.

#### Scenario: Mohist Model selector opens and allows selection
- **WHEN** user clicks the Mohist Model selector button
- **THEN** a popover panel appears showing available models grouped by provider
- **AND** user can type to filter models by name or id
- **AND** user can click a model to select it
- **AND** the popover closes after selection

#### Scenario: Coder Model selector opens and allows selection
- **WHEN** user clicks the Coder Model selector button
- **THEN** a popover panel appears showing available coder models
- **AND** user can search, select, and clear the selection

#### Scenario: Stage Model Override selectors work
- **WHEN** user expands "Stage Model Overrides"
- **AND** clicks a stage model selector
- **THEN** a popover panel appears showing available coder models for that stage
- **AND** user can select a model override for the stage

#### Scenario: Popover Panel is present in DOM when opened
- **WHEN** user clicks any ModelSelect button on the AI settings page
- **THEN** the `Popover.Panel` element is rendered in the DOM
- **AND** the panel is visible and interactive
