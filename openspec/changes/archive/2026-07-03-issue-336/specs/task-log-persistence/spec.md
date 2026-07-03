### Requirement: Task log storage is independent of workflow status adjudication

Task logs SHALL be stored in a dedicated `TaskLogEntries` store under `Infrastructure/Data/Runner/`, completely apart from the `WorkflowRun` aggregate, `WorkResult`, and the `report` channel. Writing a task log SHALL NOT pass through any workflow grain, SHALL NOT be forwarded to `WorkflowGrain`, and SHALL NOT influence task success/failure adjudication. The `report` request body, `WorkResult`, and the `WorkflowRun` aggregate structure SHALL remain unchanged — TaskLog is review evidence associated with a work item only, mirroring how artifacts are persisted independently.

#### Scenario: A task log upload never touches the workflow grain

- **WHEN** the runner uploads a task log for a work item
- **THEN** the server SHALL persist it directly to the dedicated store
- **AND** the upload SHALL NOT invoke `WorkflowGrain` or `RunnerGrain.ReportWorkflowResultAsync`
- **AND** the work item's task status SHALL be unaffected by the log's existence or content

#### Scenario: The report and WorkResult contracts are unchanged

- **WHEN** the runner reports a work item result after this change
- **THEN** the `report` request body and `WorkResult` SHALL carry no task-log field
- **AND** existing report consumers SHALL observe no structural difference

### Requirement: Upload routes by owner kind mirror artifact uploads

The server SHALL expose an internal upload endpoint at `POST /api/{workflow-runs|agent-jobs}/{ownerId}/work/{workId}/task-log` that writes buffered entries to the dedicated store, mirroring the artifact-upload routing. Owner-kind routing SHALL be symmetric with artifacts: `workflow` work SHALL address its `workflowRunId` as owner via the `/api/workflow-runs/...` route, and `agent-job` work SHALL address its `agentJobId` as owner via the `/api/agent-jobs/...` route. The store SHALL distinguish owners by an `OwnerKind` + `OwnerId` pair rather than overloading a single workflow-run id column, so the two owner kinds cannot collide.

#### Scenario: A workflow-run-owned task log is stored under its workflow run

- **WHEN** the runner uploads a task log for a workflow-owned work item via `/api/workflow-runs/{workflowRunId}/work/{workId}/task-log`
- **THEN** the server SHALL persist the entries with owner kind `workflow` and the given `workflowRunId`

#### Scenario: An agent-job-owned task log is stored under its agent job

- **WHEN** the runner uploads a task log for an agent-job-owned work item via `/api/agent-jobs/{agentJobId}/work/{workId}/task-log`
- **THEN** the server SHALL persist the entries with owner kind `agent-job` and the given `agentJobId`
- **AND** the entries SHALL NOT be conflated with any workflow run

### Requirement: Each stored entry preserves seq, timestamp, source, and masked text

The `TaskLogEntries` store SHALL persist, per entry, the work-scoped monotonic `seq`, a `timestamp`, the `source` label, and the (already masked) `text`, indexed by `(OwnerKind, OwnerId, WorkId, Seq)`. There SHALL be no stream column, consistent with merged stdout/stderr. Stored text SHALL already be the masked form produced by the runner sink, because masking occurs at capture time.

#### Scenario: Stored entries are queryable in seq order

- **WHEN** the store is queried for a work item's entries ordered by seq
- **THEN** the entries SHALL be returned in ascending `seq` order
- **AND** each entry SHALL expose its `seq`, `timestamp`, `source`, and `text`

### Requirement: The issue-path query returns lines with cursor pagination and truncation status

The server SHALL expose `GET /api/projects/{projectId}/issues/{number}/workflow/tasks/{taskId}/logs` (with optional `cursor` and `limit`) that resolves the issue's workflow run and the task, then returns `{ lines, nextCursor, truncated }`. Pagination SHALL be cursor-based over `seq` so ordering is stable and resumable; `nextCursor` SHALL be `null` when the end is reached. The response SHALL report `truncated` so a client can signal that head lines were dropped. The query path SHALL be the issue path (consistent with artifact queries), because the web holds `issueNumber` + `taskId`, not a workflow-run id.

#### Scenario: A paginated query returns a page and a next cursor

- **WHEN** a client requests the logs for a task with a `limit` smaller than the total line count
- **THEN** the server SHALL return one page of lines in seq order
- **AND** the response SHALL carry a non-null `nextCursor` to fetch the following page

#### Scenario: The final page reports a null next cursor

- **WHEN** a client requests the logs using the last page's cursor
- **THEN** the server SHALL return the remaining lines
- **AND** `nextCursor` SHALL be `null`

#### Scenario: A truncated log reports its truncation status

- **WHEN** a task's log was truncated at capture time (head dropped) and the client queries its logs
- **THEN** the response SHALL report `truncated` as true
- **AND** the returned lines SHALL be the retained tail lines

#### Scenario: A task with no log returns an empty result

- **WHEN** a client queries the logs for a task that produced no captured lines
- **THEN** the server SHALL return an empty `lines` array with a null `nextCursor`
- **AND** the response SHALL NOT be an error
