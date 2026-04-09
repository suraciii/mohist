## ADDED Requirements

### Requirement: Explore to Plan trigger mechanism
The system SHALL provide a mechanism for users to trigger the transition from Explore phase to Plan phase.

#### Scenario: Manual trigger via UI
- **GIVEN** an issue is in the "Explore" phase
- **WHEN** a user clicks the "Start Design" button in the UI
- **THEN** the system SHALL transition the issue to the "Plan" phase
- **AND** initiate the Plan phase execution

### Requirement: Explore phase conversation context
The system SHALL maintain the conversation context from the Explore phase for use in subsequent phases.

#### Scenario: Context available in Plan phase
- **GIVEN** a user has completed an Explore session
- **WHEN** the Plan phase begins
- **THEN** the system SHALL make the Explore conversation history available to Plan agents
- **AND** the Plan agents SHALL use this context for design decisions

### Requirement: Explore session persistence
The system SHALL persist Explore session messages for reference.

#### Scenario: Store explore messages
- **WHEN** messages are exchanged in the Explore phase
- **THEN** the system SHALL store the messages in the database
- **AND** associate them with the issue

### Requirement: Requirement clarity detection
The system MAY support automatic detection of when requirements are clear enough to proceed to Plan phase.

#### Scenario: Agent suggests transition (optional)
- **GIVEN** an Explore conversation has occurred
- **WHEN** the system detects sufficient clarity
- **THEN** the system MAY suggest to the user: "Requirements appear clear. Start design phase?"
- **AND** the user SHALL have the final decision to proceed
