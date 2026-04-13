## ADDED Requirements

### Requirement: Model selector UI
The system SHALL provide a model selector component in the Explore page header that allows users to view and select available models.

#### Scenario: Display model selector
- **WHEN** the user navigates to an explore session
- **THEN** the system displays the currently selected model name in the header
- **AND** the user can click to open the model selector popover

#### Scenario: Model selector popover content
- **WHEN** the user opens the model selector
- **THEN** the system displays a searchable list of available models
- **AND** models are grouped by provider
- **AND** recently used models are shown at the top

### Requirement: Model search and filter
The system SHALL support fuzzy search through the model list.

#### Scenario: Search models
- **WHEN** the user types in the model selector search box
- **THEN** the system filters models by fuzzy matching against model names and IDs
- **AND** matching models are displayed in real-time

#### Scenario: Empty search results
- **WHEN** the user searches for a term with no matching models
- **THEN** the system displays an empty state message

### Requirement: Model selection
The system SHALL allow users to select a model for the current explore session.

#### Scenario: Select a model
- **WHEN** the user clicks on a model in the selector
- **THEN** the system updates the session's model
- **AND** closes the selector popover
- **AND** the selected model is displayed in the header

#### Scenario: Model with variants
- **WHEN** the user selects a model that has variants
- **THEN** the system displays variant options
- **AND** the user can select a specific variant or use default

### Requirement: Recently used models
The system SHALL track and display recently used models.

#### Scenario: Track recent models
- **WHEN** the user selects a model
- **THEN** the system adds it to the recent models list
- **AND** recent models are persisted in localStorage

#### Scenario: Display recent models
- **WHEN** the user opens the model selector
- **THEN** the system displays up to 5 recently used models at the top
- **AND** recent models are sorted by most recently used first

### Requirement: Model badges
The system SHALL display badges for models with special properties.

#### Scenario: Display free badge
- **WHEN** a model is marked as free in the registry
- **THEN** the system displays a "Free" badge next to the model name

#### Scenario: Display latest badge
- **WHEN** a model is marked as the latest version in the registry
- **THEN** the system displays a "Latest" badge next to the model name
