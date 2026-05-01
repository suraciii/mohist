## ADDED Requirements

### Requirement: AgentRunnerService shutdown aborts all active agents

When `AgentRunnerService.shutdown()` is called, the system SHALL abort all tracked active agents by calling `abortController.abort()` on each, then clear all in-memory tracking maps (`activeAgents`, `pendingGates`, `waitingQuestions`).

#### Scenario: Shutdown with running agents

- **WHEN** `shutdown()` is called and `activeAgents` Map contains entries
- **THEN** the system SHALL call `abortController.abort()` for each active agent
- **AND** the system SHALL clear `activeAgents`, `pendingGates`, and `waitingQuestions`
- **AND** no agent subprocesses SHALL remain running after shutdown

#### Scenario: Shutdown with no running agents

- **WHEN** `shutdown()` is called and `activeAgents` Map is empty
- **THEN** the system SHALL clear `pendingGates` and `waitingQuestions`
- **AND** `shutdown()` completes without error

#### Scenario: Abort errors are caught gracefully

- **WHEN** `abortController.abort()` throws an exception for a particular agent
- **THEN** the error is caught and logged
- **AND** the system continues aborting remaining agents
- **AND** `shutdown()` still clears all maps
