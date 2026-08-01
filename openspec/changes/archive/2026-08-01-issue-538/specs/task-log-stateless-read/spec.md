### Requirement: Log upload resolves active work and publish scope without State

The task-log upload path (`TaskLogService.AppendAsync`) SHALL resolve active-work membership
and the real-time publish scope from the run-work projection, not by deserializing
WorkflowRun `State`. This replaces the two full `State` loads the upload previously performed
(active-work gate and publish-scope resolution).

#### Scenario: upload accepted for active work

- **WHEN** a runner uploads a log batch for owner kind `workflow`, owner id = a run id, and a
  `workId` whose active-work identity names that runner as the owning worker
- **THEN** the batch is persisted and the upload is accepted, with no `State` deserialization

#### Scenario: upload rejected for inactive or unknown work

- **WHEN** a runner uploads a log batch for a `workId` that is not the run's current active
  work for that runner, or the run does not exist
- **THEN** the batch is not persisted, no real-time fan-out is attempted, and the upload is
  rejected (surfaced as a 4xx), with no `State` deserialization

### Requirement: Publish scope preserves workId-to-taskId and projectId resolution

For an accepted workflow-owned upload, the publish-scope resolution SHALL resolve the
`workId → taskId` mapping from the run-work projection and, only when a `taskId` is resolved,
the `projectId` from the existing `WorkflowRunRow.MetadataProjectId` projection column. When the
`workId` cannot be mapped to a `taskId` (including checks workIds, which are not `TaskRun`s; or
the owner is not workflow-owned), the publish scope SHALL be null as a whole — both `taskId` and
`projectId` absent — matching today's behavior where `ResolvePublishScopeAsync` returns `null`
rather than a partial scope. The publisher SHALL treat a null `taskId` as "no matching
subscription, no fan-out." Fan-out remains best-effort and never blocks persistence.

#### Scenario: publish scope stamped with taskId and projectId

- **WHEN** an accepted upload's `workId` maps to a task in the run-work projection
- **THEN** the published delta envelope carries that `taskId` and the run's `projectId`, with
  no `State` deserialization

#### Scenario: unmappable workId yields a null scope and no fan-out

- **WHEN** an accepted upload's `workId` has no entry in the run-work projection (a checks
  workId, or any non-task workId; or the owner kind is not workflow)
- **THEN** the publish scope is null (both `taskId` and `projectId` absent), and no subscriber
  is notified

### Requirement: Log query resolves taskId to workId without State

The task-log query path (`TaskLogService.QueryByTaskIdAsync`) SHALL resolve `taskId → workId`
from the run-work projection, not by deserializing WorkflowRun `State`. When the run, the
task id, or the resulting work id cannot be located, the query SHALL return an empty page
(never an error), identical to today's contract.

#### Scenario: query returns the work's log page

- **WHEN** a query supplies a run id and a `taskId` present in that run's work-surface mapping
- **THEN** the corresponding work's stored log page is returned, with no `State` deserialization

#### Scenario: unresolvable taskId returns an empty page

- **WHEN** a query supplies a run id and a `taskId` not present in the run's mapping, or the
  run does not exist
- **THEN** an empty page is returned without raising an error and without `State` deserialization

### Requirement: The task-log read path never deserializes WorkflowRun State

Serving a task-log upload or a task-log query for a workflow-owned run MUST NOT cause the
run's `State` JSON to be deserialized into a `WorkflowRun`. This invariant SHALL hold for the
active-work gate, the publish-scope resolution, and the `taskId → workId` query. The
agent-job ownership branch is unchanged (it never loaded `State`).

#### Scenario: upload and query are State-free end to end

- **WHEN** a workflow-owned log upload and a workflow-owned log query are served
- **THEN** neither request triggers `State` deserialization, while acceptance, persistence,
  fan-out scope, and pagination remain identical to the prior contract
