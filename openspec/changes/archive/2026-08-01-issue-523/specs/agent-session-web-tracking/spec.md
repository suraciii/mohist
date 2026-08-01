### Requirement: Session context and current state
The Web SHALL identify an AgentSession by its stable Session ID and explain its source before showing the raw transcript. It MUST show the associated Agent or Workflow work when that association exists, the current activity state, the current turn and its inputs when present, the latest terminal result or failure evidence, and the operations that are currently safe to perform. It MUST NOT fabricate a Workflow or Agent association that the Session does not have.

#### Scenario: Agent-launched Session has no Workflow association
- **WHEN** a user opens an Agent-launched Session without a Workflow association
- **THEN** the page identifies its Agent and Session state, links back to the Agent or recorded context, and does not display a fabricated Workflow stage or run

#### Scenario: Workflow Session is opened
- **WHEN** a user opens a Session created for Workflow work
- **THEN** the page identifies its associated Issue or Workflow context, current state, current turn, latest result, and safe operations before the transcript

### Requirement: Input, turn, and transcript evidence
The Web SHALL present SessionInputs, AgentTurns, transcript messages, and tool activity in their recorded order. Each displayed user input MUST show its acceptance and delivery state and MUST be associated with the AgentTurn that processed it; multiple inputs accepted for one turn MUST remain grouped with that turn. The page MUST expose the resolved model, usage, context health, compaction history when recorded, and actionable tool or execution failure evidence.

#### Scenario: Multiple inputs are handled by one turn
- **WHEN** a Session has multiple accepted inputs assigned to the same AgentTurn
- **THEN** the page presents the inputs with their individual delivery states under that turn and retains the chronological transcript and tool activity

#### Scenario: A tool call fails during a turn
- **WHEN** recorded Session evidence contains a failed tool call or execution failure
- **THEN** the page identifies the failure and makes the corresponding failure evidence reachable from the Session view

### Requirement: Authoritative activity and incomplete information
The Web SHALL converge its Session state from authoritative reads and live Session events. It MUST distinguish active, idle, and unknown activity, and it MUST distinguish an active Session that has not produced content from an idle Session with no content. While activity, input acceptance, or a turn outcome is unknown, the page MUST preserve that uncertainty and MUST NOT represent the Session as safely idle, the input as accepted, or the turn as terminal.

#### Scenario: Runtime activity cannot be confirmed
- **WHEN** the authoritative Session observation reports unknown activity
- **THEN** the page displays an unknown state, preserves any recorded evidence, and does not present unavailable controls as safe to use

#### Scenario: Live terminal event arrives
- **WHEN** a live event reports that a current turn completed, failed, or was cancelled
- **THEN** the page converges to the authoritative terminal state and displays the resulting Session evidence without requiring a page reload
