## ADDED Requirements

### Requirement: Session model storage
The system SHALL persist the selected model for each explore session.

#### Scenario: Create session with default model
- **WHEN** a new explore session is created
- **THEN** the system sets the session's model to the global default model
- **AND** the model is stored in the explore_sessions table

#### Scenario: Update session model
- **WHEN** the user selects a different model for a session
- **THEN** the system updates the session's model in the database
- **AND** the variant is also updated if applicable

#### Scenario: Persist variant selection
- **WHEN** the user selects a model variant
- **THEN** the system stores the variant alongside the model
- **AND** the variant is used in subsequent API calls

### Requirement: Session model API
The system SHALL expose an API endpoint to update a session's model.

#### Scenario: POST /api/explore/:id/model
- **WHEN** the frontend sends a POST request to /api/explore/:id/model
- **AND** the request body contains { model: string, variant?: string }
- **THEN** the system validates the model exists and is available
- **AND** updates the session's model in the database
- **AND** returns the updated session

#### Scenario: Invalid model
- **WHEN** the user attempts to set an invalid or unavailable model
- **THEN** the system returns a 400 error with an appropriate message

### Requirement: Model retrieval on session load
The system SHALL use the session's persisted model when running the explore agent.

#### Scenario: Use session model
- **WHEN** the explore agent runs for a session
- **THEN** it uses the session's model if one is configured
- **AND** falls back to the global default if no session model is set

#### Scenario: Backward compatibility
- **WHEN** loading an old session without a model field
- **THEN** the system uses the global default model
- **AND** the session continues to work normally

### Requirement: Model display in session list
The system SHALL display the model information in the explore sessions list.

#### Scenario: Show model in session card
- **WHEN** viewing the list of explore sessions
- **THEN** each session card displays the model name currently assigned to that session

### Requirement: Variant persistence
The system SHALL support persisting and retrieving model variants per session.

#### Scenario: Store variant with model
- **WHEN** the user selects a model with a specific variant
- **THEN** both model and variant are stored together
- **AND** the variant is passed to the LLM SDK

#### Scenario: Default variant
- **WHEN** the user selects a model without specifying a variant
- **THEN** the system stores null or "default" as the variant
- **AND** the provider's default variant is used
