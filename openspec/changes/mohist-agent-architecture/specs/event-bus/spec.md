## ADDED Requirements

### Requirement: Event bus
The system SHALL implement an in-memory event bus using callback-based listeners (Set<listener>). The bus SHALL support: publishing events with a type and data payload, subscribing to events by type, and unsubscribing listeners.

#### Scenario: Publish and subscribe
- **WHEN** an event is published to the bus
- **THEN** all subscribers for that event type SHALL receive the event
- **THEN** the subscriber callback SHALL be invoked with the event data

#### Scenario: Unsubscribe
- **WHEN** a subscriber unsubscribes from an event type
- **THEN** it SHALL no longer receive events of that type

### Requirement: Event types
The system SHALL define the following event types: `stage_enter`, `stage_exit`, `agent_spawn`, `agent_done`, `question_asked`, `question_replied`, `user_message`, `user_command`, `channel_connected`, `channel_disconnected`. Each event SHALL carry an `issueId` field for routing.

#### Scenario: Event with issueId
- **WHEN** any event is published
- **THEN** the event payload SHALL include an issueId field
- **THEN** subscribers CAN filter by issueId

### Requirement: Workflow log persistence
All workflow-relevant events (stage_enter, stage_exit, agent_spawn, agent_done, user_command) SHALL be automatically persisted to the `workflow_log` table as append-only records. This SHALL be done by a dedicated log subscriber on the event bus.

#### Scenario: Auto-persist events
- **WHEN** a stage_enter event is published
- **THEN** the log subscriber SHALL append a record to workflow_log with timestamp, event type, and data

#### Scenario: Event log is append-only
- **WHEN** workflow events are recorded
- **THEN** records SHALL only be appended, never modified or deleted

### Requirement: Channel as event consumer
Channels (CLI, WeChat, Telegram) SHALL connect to the event bus as consumers. Each channel SHALL subscribe to relevant events and render them to the user. Channels SHALL also produce events (user_message, user_command) when the user interacts.

#### Scenario: CLI channel subscribes
- **WHEN** the CLI channel connects via `mo attach`
- **THEN** it SHALL subscribe to events for the attached issue
- **THEN** it SHALL render agent outputs and questions to the terminal

#### Scenario: Channel produces user input
- **WHEN** the user types in the CLI channel
- **THEN** the channel SHALL publish a user_message event to the bus
- **THEN** the Main Agent session SHALL receive the message
