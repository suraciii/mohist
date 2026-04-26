## MODIFIED Requirements

### Requirement: Frontend agentStatus uses issueNumber field for matching

The frontend SSE event handlers and agent status detection SHALL use `activeAgents` array for per-issue matching. The `AgentStatus` type SHALL include `activeAgents` array (each entry with `issueId`, `issueNumber`, `projectId`) and `maxConcurrentAgents` number. The legacy `running`, `issueId`, `issueNumber` single-agent fields SHALL remain for backward compatibility but frontend components SHALL prefer `activeAgents` for all per-issue decisions.

#### Scenario: Agent running detection for specific issue
- **WHEN** agent is running on issue #5
- **THEN** `activeAgents` array contains an entry with `issueNumber === 5`
- **AND** the hook for issue #5 detects running state
- **AND** the hook for issue #3 does NOT detect running state

#### Scenario: SSE event filtering works correctly
- **WHEN** a `coder_text_chunk` SSE event arrives with `issueId: "5"`
- **AND** the user is viewing issue number 5
- **THEN** the event passes the filter and is processed

#### Scenario: SSE event for different issue filtered out
- **WHEN** a `coder_text_chunk` SSE event arrives with `issueId: "2"`
- **AND** the user is viewing issue number 5
- **THEN** the event is filtered out

#### Scenario: Multiple agents running simultaneously
- **WHEN** agents are running on issue #3 and issue #7
- **THEN** `activeAgents` array has two entries
- **AND** issue #3 detail page detects its agent is running
- **AND** issue #7 detail page detects its agent is running
- **AND** issue #5 detail page detects no agent running for it
