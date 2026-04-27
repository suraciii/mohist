## ADDED Requirements

### Requirement: Force Stop API endpoint

Server SHALL provide `POST /api/issues/:number/force-stop` endpoint that forcefully terminates the running agent's child process for the specified issue, sets the issue status to `interrupted`, and cleans up all associated resources.

#### Scenario: Force stop running agent
- **WHEN** `POST /api/issues/5/force-stop` is called
- **AND** issue #5 has an active agent running
- **THEN** the agent's ACP child process is killed (SIGKILL)
- **AND** the issue status is set to `interrupted`
- **AND** the issue stage is preserved (not reset to draft)
- **AND** the `activeAgents` entry for issue #5 is removed
- **AND** any pending gates and waiting questions for issue #5 are cleared
- **AND** an `agent_stopped` event is emitted via EventBus with `{ issueId, projectId, issueNumber, reason: "force_stop" }`
- **AND** the response returns 200 with `{ ok: true, issueNumber: 5 }`

#### Scenario: Force stop issue not running
- **WHEN** `POST /api/issues/5/force-stop` is called
- **AND** issue #5 has no active agent
- **THEN** the response returns 409 with `{ error: "No agent running for issue #5" }`

#### Scenario: Force stop issue does not exist
- **WHEN** `POST /api/issues/999/force-stop` is called
- **AND** issue #999 does not exist
- **THEN** the response returns 404 with `{ error: "Issue not found" }`

### Requirement: RunningAgent stores child process reference

The `RunningAgent` interface SHALL include an optional `childProcess` field holding a reference to the ACP subprocess (`ChildProcess | undefined`). When a pipeline is executing, the child process SHALL be stored so that `forceStop()` can kill it.

#### Scenario: Pipeline execution stores child process
- **WHEN** `executePipeline()` starts a pipeline and the ACP subprocess is spawned
- **THEN** the spawned `ChildProcess` is stored in `RunningAgent.childProcess`

#### Scenario: Child process cleared after pipeline completes
- **WHEN** a pipeline completes (success, error, or gate pause)
- **THEN** `RunningAgent.childProcess` is cleared (set to undefined) in the finally block

### Requirement: AgentRunnerService forceStop method

`AgentRunnerService` SHALL provide a `forceStop(issueId: string)` method that kills the child process, removes the agent from `activeAgents`, clears associated state, and emits an event. The method SHALL be idempotent — calling it twice for the same issue SHALL not throw.

#### Scenario: Force stop kills child process
- **WHEN** `forceStop(issueId)` is called with a running agent's issueId
- **THEN** `RunningAgent.childProcess?.kill('SIGKILL')` is called
- **AND** the agent entry is removed from `activeAgents`
- **AND** pending gates and waiting questions for that issueId are cleared
- **AND** the method returns `{ stopped: true, issueNumber }`

#### Scenario: Force stop on already-removed agent
- **WHEN** `forceStop(issueId)` is called and the agent has already been removed from `activeAgents`
- **THEN** the method returns `{ stopped: false }` without error

### Requirement: Frontend API client provides forceStop method

`api.ts` SHALL add a `forceStopIssue(issueNumber: number)` method corresponding to `POST /api/issues/:number/force-stop`.

#### Scenario: forceStopIssue API call
- **WHEN** `api.forceStopIssue(5)` is called
- **THEN** a `POST /api/issues/5/force-stop` request is sent
- **AND** the response is returned
