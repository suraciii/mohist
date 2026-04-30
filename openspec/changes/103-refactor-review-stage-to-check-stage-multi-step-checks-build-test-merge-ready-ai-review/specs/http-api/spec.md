## MODIFIED Requirements

### Requirement: Status API reflects stage model

The status API SHALL report stages used in the current pipeline: draft, plan, build, check, done, backlog, explore. The response SHALL NOT include the `review` stage key. The `issuesByStage` response SHALL use `check` instead of `review`.

#### Scenario: Get current project status
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** the response SHALL include `issuesByStage` with `draft`, `plan`, `build`, `check`, `done`, `backlog`, `explore` counts
- **AND** the response SHALL NOT include a `review` key in `issuesByStage`

#### Scenario: ServerState has no task fields
- **WHEN** the ServerState interface is inspected
- **THEN** it SHALL NOT contain `activeTasks` or `queuedTasks`

#### Scenario: Issue show endpoint includes check suite output
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **AND** issue is in `check` stage with `approvalState.status === 'awaiting'`
- **THEN** response SHALL include `approvalState.output` containing the `CheckSuiteOutput` object
- **AND** `CheckSuiteOutput.checks` array SHALL contain per-check results
