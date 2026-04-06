## Requirements

### Requirement: Concurrent agent execution

The AgentRunnerService SHALL support running multiple agents concurrently up to the configured `maxConcurrentAgents` limit. When `maxConcurrentAgents` is reached, new agents SHALL be queued.

#### Scenario: Start agent under capacity
- **WHEN** `start()` is called and `activeAgents.size < maxConcurrentAgents`
- **THEN** the agent SHALL start immediately
- **AND** `activeAgents` SHALL contain the new running agent

#### Scenario: Start agent at capacity
- **WHEN** `start()` is called and `activeAgents.size >= maxConcurrentAgents`
- **THEN** the agent request SHALL be added to the queue
- **AND** the start() method SHALL return without blocking

#### Scenario: Agent completes with queue waiting
- **WHEN** an agent completes and `agentQueue.length > 0`
- **THEN** the next queued agent SHALL be started automatically
- **AND** the queue SHALL be processed in FIFO order

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
- **THEN** the system SHALL return information about all active agents
- **AND** include queue depth

### Requirement: Queue processing

Queued agent requests SHALL be processed in FIFO order when capacity becomes available.

#### Scenario: FIFO ordering
- **WHEN** agent A, B, C are queued in that order
- **AND** an agent completes
- **THEN** agent A SHALL be started first

#### Scenario: Queue position in response
- **WHEN** a start request is queued
- **THEN** the response SHALL indicate the queue position
