## ADDED Requirements

### Requirement: AgentStatus includes pipeline progress metadata

`GET /api/agent/status` response and `AgentStatus` interface SHALL include a `progress` field on each `activeAgents` entry. The `progress` field SHALL contain: `stage` (current pipeline stage string), `roundType` (current plan/review round, e.g. `proposal`, `specs`, `design`, `tasks`), `roundIndex` (0-based index), `taskProgress` (`{ completed: number, total: number }` for Build stage, null otherwise), and `lastActivityAt` (ISO 8601 timestamp of the most recent activity).

#### Scenario: Agent running Plan stage
- **WHEN** an agent is running Plan stage, currently on the `design` round (index 2)
- **THEN** `GET /api/agent/status` returns `activeAgents[0].progress.stage === "plan"`
- **AND** `activeAgents[0].progress.roundType === "design"`
- **AND** `activeAgents[0].progress.roundIndex === 2`
- **AND** `activeAgents[0].progress.taskProgress === null`
- **AND** `activeAgents[0].progress.lastActivityAt` is a valid ISO 8601 string

#### Scenario: Agent running Build stage with task progress
- **WHEN** an agent is running Build stage and 2 of 5 tasks are complete
- **THEN** `activeAgents[0].progress.stage === "build"`
- **AND** `activeAgents[0].progress.taskProgress === { completed: 2, total: 5 }`
- **AND** `activeAgents[0].progress.lastActivityAt` reflects the most recent coder session or task update

#### Scenario: Agent running Review stage
- **WHEN** an agent is running Review stage, currently on round `review` (index 0)
- **THEN** `activeAgents[0].progress.stage === "review"`
- **AND** `activeAgents[0].progress.roundType === "review"`
- **AND** `activeAgents[0].progress.roundIndex === 0`

#### Scenario: Agent just started, no stage progress yet
- **WHEN** an agent has just been started and no stage-specific activity has occurred yet
- **THEN** `activeAgents[0].progress.stage` matches the issue's current stage
- **AND** `activeAgents[0].progress.lastActivityAt` is set to the agent start time

### Requirement: RunningAgent carries mutable progress state

The `RunningAgent` interface SHALL include a mutable `progress` object with fields: `stage`, `roundType`, `roundIndex`, `taskProgress`, and `lastActivityAt`. These fields SHALL be updated in-band by `WorkflowController` during pipeline execution — no polling or separate queries.

#### Scenario: WorkflowController updates progress on stage entry
- **WHEN** `WorkflowController.run()` enters the Plan stage
- **THEN** the corresponding `RunningAgent.progress.stage` is set to `"plan"`
- **AND** `lastActivityAt` is updated to the current time

#### Scenario: WorkflowController updates progress on round change
- **WHEN** Plan stage transitions from `specs` round (index 1) to `design` round (index 2)
- **THEN** `RunningAgent.progress.roundType` is set to `"design"`
- **AND** `RunningAgent.progress.roundIndex` is set to `2`
- **AND** `RunningAgent.progress.lastActivityAt` is updated

#### Scenario: RalphExecutor updates task progress
- **WHEN** RalphExecutor completes task T-003 out of 5 total tasks during Build stage
- **THEN** `RunningAgent.progress.taskProgress` is set to `{ completed: 3, total: 5 }`
- **AND** `RunningAgent.progress.lastActivityAt` is updated

### Requirement: getStatus returns enriched progress for each active agent

`AgentRunnerService.getStatus()` SHALL include the `progress` field from each `RunningAgent` in the `activeAgents` array of the response. The progress data SHALL be a snapshot read — no lock required.

#### Scenario: Multiple agents running concurrently
- **WHEN** two agents are running: agent A in Build (3/5 tasks) and agent B in Plan (specs round)
- **THEN** `getStatus().activeAgents` contains two entries
- **AND** agent A entry has `progress.taskProgress === { completed: 3, total: 5 }`
- **AND** agent B entry has `progress.roundType === "specs"` and `progress.taskProgress === null`

### Requirement: acp-session cleanup has defensive timeout

The `cleanup()` function in `acp-session.ts` SHALL wrap its `Promise.allSettled` stream cancellation with a 5-second timeout. If the stream operations do not settle within 5 seconds, the cleanup SHALL resolve regardless, and `ensureKill()` SHALL still be called.

#### Scenario: Stream cleanup completes within timeout
- **WHEN** `cleanup()` is called and `stream.readable.cancel()` and `stream.writable.abort()` settle within 5 seconds
- **THEN** cleanup resolves normally with the settled results
- **AND** `ensureKill()` is called

#### Scenario: Stream cleanup hangs
- **WHEN** `cleanup()` is called and the stream operations do not settle within 5 seconds
- **THEN** cleanup resolves after the 5-second timeout
- **AND** `ensureKill()` is still called
- **AND** a warning is logged indicating cleanup timed out
