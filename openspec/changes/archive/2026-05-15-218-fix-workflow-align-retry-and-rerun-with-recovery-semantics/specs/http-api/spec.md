## ADDED Requirements

### Requirement: REQ-HTTP-RECOVERY-001 Retry endpoint uses WorkflowRun failed work

`POST /api/issues/:number/retry` SHALL base retry availability on the latest WorkflowRun current-stage retryable failed work instead of requiring `tasks.json` or a checkpoint to exist. The endpoint SHALL return distinguishable errors for no failed WorkflowRun, no retryable failed work, and missing required project/worktree/change artifacts.

#### Scenario: Retry Plan failure before tasks file exists
- **WHEN** the latest WorkflowRun failed in Plan while generating `tasks.json`
- **AND** `tasks.json` does not exist yet
- **THEN** `POST /api/issues/:number/retry` accepts the retry
- **AND** pipeline recovery is queued from the failed Plan work
- **AND** the response does not claim a checkpoint is required

#### Scenario: Retry unavailable reasons are distinct
- **WHEN** `POST /api/issues/:number/retry` cannot proceed
- **THEN** no failed WorkflowRun, no retryable failed work, missing worktree, and missing change artifacts are returned as distinguishable errors
- **AND** each error gives enough guidance for the user to choose retry, rerun, inspect artifacts, or intervene manually

### Requirement: REQ-HTTP-RECOVERY-002 Rerun endpoint restarts current stage

`POST /api/issues/:number/rerun` SHALL apply current-stage rerun semantics rather than retry semantics. The endpoint SHALL clear current-stage checkpoint and recovery state, preserve earlier passed stages, keep the current stage unchanged, and queue execution from the first current-stage work item.

#### Scenario: Rerun failed current stage from first work
- **WHEN** `POST /api/issues/:number/rerun` is called for a failed or blocked issue in a runnable stage
- **THEN** the endpoint clears current-stage checkpoint, failure, approval, blocked reason, and retry count
- **AND** the current stage remains unchanged
- **AND** earlier passed stages are not rerun
- **AND** pipeline recovery is queued from the first work item of the current stage
