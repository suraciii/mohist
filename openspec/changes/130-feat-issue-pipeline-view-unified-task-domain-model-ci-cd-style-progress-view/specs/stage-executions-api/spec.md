## ADDED Requirements

### Requirement: Issue stage executions endpoint

Server SHALL provide `GET /api/issues/:number/executions` endpoint that returns all stage execution records for the specified issue.

#### Scenario: Get executions for active issue

- **WHEN** a client requests `GET /api/issues/5/executions`
- **AND** issue #5 has completed Plan (passed) and is in Build (running)
- **THEN** the response has status 200
- **AND** the body contains `{ data: [ { id, stage: "plan", status: "passed", taskResults: StageTaskResult[], checkResults: CheckResult[], createdAt, updatedAt }, { id, stage: "build", status: "running", taskResults: StageTaskResult[], checkResults: [], createdAt, updatedAt } ] }`
- **AND** records are ordered by `createdAt` ascending

#### Scenario: Get executions for issue with escalation history

- **WHEN** issue #3 has a Build execution that failed and triggered escalation back to Plan
- **AND** then a second Plan execution and a second Build execution (passed)
- **THEN** `GET /api/issues/3/executions` returns 4+ records including all historical executions
- **AND** the ordering reflects chronological progression

#### Scenario: Get executions for draft issue

- **WHEN** a client requests `GET /api/issues/1/executions`
- **AND** issue #1 has never been started (stage: draft)
- **THEN** the response has status 200 with `{ data: [] }`

#### Scenario: Issue not found

- **WHEN** a client requests `GET /api/issues/999/executions`
- **AND** issue #999 does not exist
- **THEN** the response has status 404

### Requirement: Execution response includes structured task results

Each execution record in the response SHALL include `taskResults` as a `StageTaskResult[]` array and `checkResults` as a `CheckResult[]` array. The `taskResults` SHALL contain per-task records with `taskId`, `title`, `status`, `artifacts`, `attempts`, and `duration`.

#### Scenario: Plan execution task results structure

- **WHEN** a Plan execution is returned
- **AND** the Plan stage completed all 5 tasks
- **THEN** `taskResults` is an array of 5 `StageTaskResult` objects
- **AND** each has `taskId` matching one of: `proposal`, `specs`, `design`, `tasks`, `self-review`

#### Scenario: Build execution task results structure

- **WHEN** a Build execution is returned with T-001 completed and T-002 failed
- **THEN** `taskResults` contains 2 entries
- **AND** T-001 entry has `{ status: 'completed', artifacts: [...], attempts: 1 }`
- **AND** T-002 entry has `{ status: 'failed', attempts: 3 }`

### Requirement: Frontend API client provides getIssueExecutions method

`api.ts` SHALL add a `getIssueExecutions(issueNumber: number)` method corresponding to `GET /api/issues/:number/executions`.

#### Scenario: getIssueExecutions successful call

- **WHEN** `api.getIssueExecutions(5)` is called
- **THEN** a `GET /api/issues/5/executions` request is sent
- **AND** the response data array is returned

#### Scenario: getIssueExecutions for non-existent issue

- **WHEN** `api.getIssueExecutions(999)` is called
- **AND** the server returns 404
- **THEN** an error is thrown or an empty result is returned per the API client's error handling convention

### Requirement: Frontend hooks provide useIssueExecutions

`useQueries.ts` SHALL add a `useIssueExecutions(issueNumber: number)` hook that wraps `GET /api/issues/:number/executions` with React Query.

#### Scenario: useIssueExecutions for active issue

- **WHEN** a component calls `useIssueExecutions(5)`
- **THEN** the hook fetches executions from the API
- **AND** returns `{ data: StageExecution[], isLoading, error }`

#### Scenario: useIssueExecutions auto-refreshes on SSE events

- **WHEN** the component is mounted and a `stage_task_update` event is received for the same issue
- **THEN** the query is invalidated and refetched to get updated task results
