### Requirement: Workflow and Agent-launch entries share one canonical command routing

Both the Workflow-scoped surface (issue + session name) and the Agent-launch-scoped surface (agent + session id) SHALL resolve to the same canonical AgentSession routing and SHALL expose the same set of session operations with identical product semantics. Compact, Reset, Follow-up, and Cancel SHALL be reachable from both sources through the canonical routing defined for AgentSession commands. Neither source SHALL offer a privileged or divergent command path.

#### Scenario: Compact and Reset are reachable from both sources

- **WHEN** a caller requests Compact or Reset through the Workflow-scoped surface and through the Agent-launch-scoped surface
- **THEN** both SHALL resolve to the canonical AgentSession and apply the same compact-keeps-binding / reset-expected-binding-guard semantics
- **AND** both SHALL return responses addressed by the same stable `sessionId`

#### Scenario: Follow-up and Cancel are reachable from both sources

- **WHEN** a caller requests Follow-up or Cancel through either source surface
- **THEN** both SHALL route through the canonical AgentSession command path
- **AND** SHALL observe the same join-active-turn / start-idle-turn and interrupt-turn-only semantics

### Requirement: Named-agent CLI gains compact and reset

The named-agent CLI (`mo agent session`) SHALL provide `compact` and `reset` subcommands that share the canonical AgentSession routing and product semantics with the Workflow-scoped session commands (`mo issue session compact|reset`). The named-agent compact and reset SHALL NOT rotate the `sessionId` and SHALL return the same stable `sessionId` in their responses.

#### Scenario: mo agent session compact is available and stable-identity

- **WHEN** a caller runs `mo agent session compact <session-id>`
- **THEN** the command SHALL route to the canonical AgentSession and apply the compact-keeps-binding semantics
- **AND** the response SHALL return the same stable `sessionId`, not a new id

#### Scenario: mo agent session reset is available and stable-identity

- **WHEN** a caller runs `mo agent session reset <session-id>`
- **THEN** the command SHALL route to the canonical AgentSession and apply the reset expected-binding-guard semantics
- **AND** the response SHALL return the same stable `sessionId`, not a new id

### Requirement: Recovery responses return the same stable sessionId with no id rotation

Compact and Reset API and CLI responses SHALL return the same stable `sessionId` the command targeted. The system SHALL NOT mint, rotate, or advertise a new session id as the result of Compact or Reset. Any code path (route handler, grain command, response mapper) that previously generated a fresh client id for a recovery command SHALL be removed.

#### Scenario: Compact response carries the unchanged sessionId

- **WHEN** Compact completes on an AgentSession
- **THEN** the API and CLI response SHALL identify the session by the same stable `sessionId` it was addressed by
- **AND** the response SHALL NOT include a rotated or newly generated session id

#### Scenario: Reset response carries the unchanged sessionId

- **WHEN** Reset completes on an AgentSession (replacement applied under the expected-binding guard)
- **THEN** the API and CLI response SHALL identify the session by the same stable `sessionId` it was addressed by
- **AND** the response SHALL NOT include a rotated or newly generated session id

### Requirement: Help text and error wording reflect the stable-identity model

CLI command descriptions and help text SHALL reflect that Compact and Reset operate in place on a stable AgentSession identity. The Workflow-scoped `mo issue session compact|reset` help text SHALL NOT advertise "return a new session id" or any id-rotation wording. Error messages for active-session conflicts and missing runtime sessions SHALL reference the stable `sessionId` and SHALL NOT mention id rotation.

#### Scenario: Compact and reset help drops new-session-id wording

- **WHEN** a caller inspects the help text for `mo issue session compact` or `mo issue session reset`
- **THEN** the description SHALL NOT contain "new session id" or any wording implying the session id is rotated
- **AND** SHALL describe an in-place operation on the existing session

#### Scenario: Conflict and missing-session errors reference the stable sessionId

- **WHEN** a Compact or Reset is rejected because the session is active, or because the current Runtime Session is missing
- **THEN** the error wording SHALL reference the stable `sessionId`
- **AND** SHALL NOT suggest that a new session id was or would be produced
