## ADDED Requirements

### Requirement: Provider list visual grouping

AI settings page SHALL display providers in visually distinct groups: "Connected" (configured providers with green indicator) at the top, followed by "Available" (unconfigured providers) in a collapsible section. The "Connected" group SHALL always be expanded. The "Available" group SHALL be collapsed by default when there are connected providers.

#### Scenario: Connected providers shown first and expanded
- **WHEN** user navigates to Settings > AI
- **AND** there are 1 or more configured providers
- **THEN** connected providers are displayed in a "Connected" group at the top of the provider list
- **AND** the "Connected" group is expanded

#### Scenario: Available providers in collapsible section
- **WHEN** user navigates to Settings > AI
- **AND** there are unconfigured providers
- **THEN** unconfigured providers are displayed in an "Available" group below the "Connected" group
- **AND** the "Available" group is collapsed by default when there are connected providers

#### Scenario: Available section expanded when no connected providers
- **WHEN** user navigates to Settings > AI
- **AND** there are zero configured providers
- **THEN** the "Available" group is expanded by default

#### Scenario: Toggling available providers section
- **WHEN** user clicks the "Available" group header
- **THEN** the available providers section toggles between expanded and collapsed

### Requirement: Model Selection section positioned above providers

AI settings page SHALL display the "Model Selection" section (Mohist Model, Coder Model, Stage Model Overrides) above the Provider list section, so the most frequently used controls are immediately visible without scrolling.

#### Scenario: Model Selection appears before Provider list
- **WHEN** user navigates to Settings > AI
- **THEN** the "Model Selection" section is rendered before the "Providers" section in the page
- **AND** Mohist Model and Coder Model selectors are visible without scrolling
