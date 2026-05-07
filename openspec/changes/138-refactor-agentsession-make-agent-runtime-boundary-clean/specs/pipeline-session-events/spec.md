## MODIFIED Requirements

### Requirement: Pipeline session events are bridged through observers

Plan and Check stage session visibility SHALL use explicit observers for raw ACP notifications instead of runtime option callbacks, while preserving existing event behavior.

#### Scenario: Plan stage reuses a multi-round session
- **WHEN** Plan generates proposal, specs, design, tasks, self-review, or retry prompts
- **THEN** the stage uses one `AgentSession` instance across those prompts
- **AND** raw ACP notifications emit `plan_session_update` with issueId, projectId, roundType, roundIndex, sessionUpdate, and data

#### Scenario: Check stage reuses a multi-round session
- **WHEN** Check runs review and review-self-check prompts
- **THEN** the stage uses one `AgentSession` instance across those prompts
- **AND** raw ACP notifications emit `plan_session_update` with review round metadata

#### Scenario: Bridge emission remains fire-and-forget
- **WHEN** the EventBus emit for a Plan or Check raw notification fails
- **THEN** the error is caught and logged
- **AND** stage execution continues normally

### Requirement: Build and service session events remain observer-driven

Build, check auto-fix, Explore, Skill, and conflict-resolution sessions SHALL attach workflow visibility observers where existing behavior requires realtime progress or persistence.

#### Scenario: Build task events remain visible
- **WHEN** RalphExecutor runs Build tasks through agent sessions
- **THEN** coder text chunks and tool calls continue to use per-task execution IDs
- **AND** realtime and persisted visibility are produced by observers

#### Scenario: Non-workflow service sessions keep progress when configured
- **WHEN** Explore, Skill, or conflict-resolution sessions are created with visibility dependencies available
- **THEN** those sessions attach observers that preserve current logs and realtime progress behavior
