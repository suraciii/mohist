## ADDED Requirements

### Requirement: ask_user tool
The system SHALL provide an ask_user tool for sub-agents to ask the user structured questions. The tool SHALL use a Deferred-based pending map (reference: opencode Question module). When called, it SHALL block the sub-agent's LLM loop until the user replies or rejects.

#### Scenario: Ask user question
- **WHEN** a sub-agent calls ask_user with a question and options
- **THEN** the system SHALL create a Deferred and store it in the pending map
- **THEN** a question_asked event SHALL be published to the event bus
- **THEN** the sub-agent's LLM loop SHALL block awaiting the Deferred

#### Scenario: User replies to question
- **WHEN** the user replies via any channel
- **THEN** a question_replied event SHALL be published to the event bus
- **THEN** the Deferred SHALL be resolved with the user's answer
- **THEN** the sub-agent's LLM loop SHALL resume with the answer

#### Scenario: User rejects question
- **WHEN** the user dismisses a question
- **THEN** the Deferred SHALL be rejected with a RejectedError
- **THEN** the sub-agent's LLM loop SHALL handle the rejection

### Requirement: mo attach command
The system SHALL provide a `mo attach <issue-id>` CLI command that connects the terminal to an issue's session as a CLI channel. The CLI channel SHALL render agent outputs to stdout and read user input from stdin.

#### Scenario: Attach to active issue
- **WHEN** the user runs `mo attach 42`
- **THEN** the CLI SHALL connect to the event bus for issue #42
- **THEN** agent outputs and questions SHALL be rendered to the terminal
- **THEN** user input from stdin SHALL be published as user_message events

#### Scenario: Detach
- **WHEN** the user presses Ctrl+C or types `exit`
- **THEN** the CLI channel SHALL disconnect from the event bus
- **THEN** the agent sessions SHALL continue running (not affected)

#### Scenario: Attach to inactive issue
- **WHEN** the user runs `mo attach` on an issue with no active session
- **THEN** the system SHALL display the issue's current status
- **THEN** it SHALL NOT connect to a session

### Requirement: Approve command
The user SHALL be able to approve a gate via any channel by sending an approve command. This SHALL resume the Main Agent session and advance to the next stage.

#### Scenario: Approve via CLI
- **WHEN** the user types an approve command while attached to an issue
- **THEN** a user_command event with type `approve` SHALL be published
- **THEN** the Main Agent SHALL resume from the gate and advance to the next stage

#### Scenario: Approve via CLI command
- **WHEN** the user runs `mo approve <issue-id>` without being attached
- **THEN** the system SHALL publish a user_command event with type `approve`
- **THEN** the Main Agent SHALL advance to the next stage

### Requirement: Rollback command
The user SHALL be able to request rollback to a previous stage via any channel.

#### Scenario: Rollback via attach
- **WHEN** the user types "回到探索" while attached to an issue
- **THEN** the Main Agent SHALL interpret this as a rollback request
- **THEN** the Main Agent SHALL handle the rollback (cancel sub-agent, update stage, spawn new sub-agent)

### Requirement: No-channel handling
When no channel is connected, the Main Agent SHALL NOT spawn sub-agents that require user interaction (ask_user). The Main Agent SHALL check for online channels before spawning sub-agents.

#### Scenario: No channel available
- **WHEN** the Main Agent is about to spawn a sub-agent that uses ask_user
- **AND** no channel is connected
- **THEN** the Main Agent SHALL wait until a channel connects
- **THEN** a channel_connected event SHALL trigger the spawn

#### Scenario: Channel connects during gate
- **WHEN** a channel connects while the Main Agent is waiting
- **THEN** a channel_connected event SHALL be published
- **THEN** the Main Agent SHALL resume its decision-making
