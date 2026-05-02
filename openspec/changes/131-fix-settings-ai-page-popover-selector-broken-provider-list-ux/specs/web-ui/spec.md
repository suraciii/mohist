## ADDED Requirements

### Requirement: Model selection popover renders correctly

The `ModelSelect` component SHALL render its dropdown panel when the user clicks the trigger button. Under `@headlessui/react` v2, `Popover.Panel` SHALL NOT be wrapped in a `Transition` without `show={open}`, as v2's `Transition` does not auto-detect Popover's open state.

#### Scenario: Mohist Model selector opens and allows selection
- **WHEN** user clicks the Mohist Model selector button
- **THEN** the popover panel renders in the DOM
- **AND** displays a searchable list of available models grouped by provider
- **AND** selecting a model updates the Mohist Model setting

#### Scenario: Coder Model selector opens and allows selection
- **WHEN** user clicks the Coder Model selector button
- **THEN** the popover panel renders in the DOM
- **AND** displays a searchable list of coder models
- **AND** selecting a model updates the Coder Model setting

#### Scenario: Stage Model Override selector opens and allows selection
- **WHEN** user expands Stage Model Overrides
- **AND** clicks any stage model selector button
- **THEN** the popover panel renders in the DOM
- **AND** selecting a model updates that stage's override

### Requirement: AI Settings page surfaces Model Selection prominently

The AI Settings page SHALL display the Model Selection section above the provider list, so users can quickly select models without scrolling past the full provider catalog.

#### Scenario: Model Selection appears above Providers
- **WHEN** user navigates to the AI Settings page
- **THEN** the Model Selection section (Mohist Model, Coder Model) renders before the Providers section

### Requirement: Provider list has visual grouping

The provider list SHALL visually separate connected providers from available (unconfigured) providers, with the connected section always visible and the available section collapsible.

#### Scenario: Connected providers display in a dedicated section
- **WHEN** at least one provider is configured
- **THEN** connected providers render in a "Connected" section with a green status indicator
- **AND** each shows provider name, masked API key, and a Remove button

#### Scenario: Available providers are collapsed by default
- **WHEN** there are unconfigured providers
- **THEN** available providers render in a collapsible "Available Providers" section
- **AND** the section is collapsed by default, showing only a summary (e.g., count)
- **AND** expanding the section reveals the full list with Connect buttons

#### Scenario: Only connected providers when no available providers
- **WHEN** all builtin providers are configured
- **THEN** the Available Providers section is not displayed
