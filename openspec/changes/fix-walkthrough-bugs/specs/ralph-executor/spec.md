## MODIFIED Requirements

### Requirement: Build stage uses UUID for workflow log
`runPipelineBuildStage()` SHALL use `issue.id` (UUID) as the issueId when writing to workflow_log, not `String(issue.number)`.

#### Scenario: Build stage writes workflow log
- **WHEN** build stage calls `writeLog()` with issueId
- **THEN** issueId is the UUID from `issue.id` (e.g. `"13e44188-2d26-4fe0-bcef-6a697fb4ad9d"`)
- **AND** workflow_log insert succeeds without FOREIGN KEY constraint failure

#### Scenario: Plan stage workflow logs unaffected
- **WHEN** plan stage writes workflow logs via multi-round ACP session
- **THEN** existing behavior is preserved (plan stage already uses correct UUID)

### Requirement: Ralph executor separates sseIssueId and logIssueId
`runRalphLoop()` SHALL use `context.issueId` (UUID) for `logIssueId` (workflow_log and logging), and `context.issueNumber` for `sseIssueId` (eventBus).

#### Scenario: Ralph loop writes task logs
- **WHEN** Ralph loop calls `writeTaskLog()` for task_started/completed/failed/retrying
- **THEN** issueId passed to workflow_log is the UUID from `context.issueId`
- **AND** workflow_log insert succeeds without FOREIGN KEY constraint failure

#### Scenario: Ralph loop emits eventBus events
- **WHEN** Ralph loop emits ralph_task_update or ralph_loop_progress events
- **THEN** issueId in event data continues to use `context.issueNumber` (number format)
- **AND** existing event consumers are unaffected
