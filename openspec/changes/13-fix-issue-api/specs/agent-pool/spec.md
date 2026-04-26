## MODIFIED Requirements

### Requirement: Concurrent agent execution

The AgentRunnerService SHALL support running multiple agents concurrently up to the configured `maxConcurrentAgents` limit. When `maxConcurrentAgents` is reached, `startPipeline()` SHALL reject the request with an error; queued execution is not yet implemented.

#### Scenario: Start agent under capacity
- **WHEN** `startPipeline()` is called and `activeAgents.size < maxConcurrentAgents`
- **THEN** the agent SHALL start immediately
- **AND** `activeAgents` SHALL contain the new running agent
- **AND** the method SHALL return `{ started: true }`

#### Scenario: Start agent at capacity
- **WHEN** `startPipeline()` is called and `activeAgents.size >= maxConcurrentAgents`
- **THEN** the method SHALL return `{ started: false, error: "..." }`
- **AND** the error message SHALL indicate that the concurrent agent limit has been reached
- **AND** the error message SHALL include the current limit value

#### Scenario: Start agent for issue already running
- **WHEN** `startPipeline()` is called and `activeAgents.has(issue.id)` is true
- **THEN** the method SHALL return `{ started: false, error: "..." }`
- **AND** the error message SHALL indicate that this issue already has a running agent

### Requirement: Per-issue agent tracking

The AgentRunnerService SHALL track agent state by issueId to support pause/cancel/resume operations.

#### Scenario: Check if specific issue is running
- **WHEN** `isRunning(issueId)` is called with an issueId
- **THEN** the system SHALL return true if that issueId has an active agent
- **AND** return false otherwise

#### Scenario: Check if any agent is running
- **WHEN** `isRunning()` is called without issueId
- **THEN** the system SHALL return true if any agent is active
- **AND** return false otherwise

#### Scenario: Get status of all active agents
- **WHEN** `getStatus()` is called
- **THEN** the system SHALL return the full `activeAgents` array (each entry with `issueId`, `issueNumber`, `projectId`)
- **AND** SHALL return `maxConcurrentAgents` number
- **AND** SHALL return `queueDepth` number

### Requirement: Queue processing

Queued agent requests SHALL be processed in FIFO order when capacity becomes available.

#### Scenario: FIFO ordering
- **WHEN** agent A, B, C are queued in that order
- **AND** an agent completes
- **THEN** agent A SHALL be started first

#### Scenario: Queue position in response
- **WHEN** a start request is queued
- **THEN** the response SHALL indicate the queue position
