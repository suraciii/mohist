### Requirement: Run-work projection content

A WorkflowRun SHALL expose a persisted projection of its work surface that is sufficient
for the task-log read path (and future read paths) to resolve work identity and active-work
membership. The projection SHALL contain, for the run:

- The run-wide `taskId ↔ workId` mapping covering every `TaskRun` in every stage, where
  `taskId` is `TaskRun.Id` and `workId` is the task's effective work identifier
  (`TaskRun.WorkId`, falling back to `TaskRun.Id` when `WorkId` is unset).
- The single current active-work identity, if any: the effective `workId` and the owning
  `workerId` of the currently-running work in the run's current stage. A run has at most one
  active work at a time (the current stage's `Running` task claimed by a worker, or active
  checks); when no work is active the projection SHALL indicate none.

The projection captures only identity scalars; it MUST NOT carry task inputs, outputs,
dispatch payloads, prompts, or any field outside the work-surface identity.

#### Scenario: mapping covers tasks across all stages

- **WHEN** a run has completed tasks in earlier stages and a running task in the current stage
- **THEN** the projection's `taskId ↔ workId` mapping contains an entry for every task in
  every stage, including completed ones, not only the current stage

#### Scenario: effective work id falls back to task id

- **WHEN** a `TaskRun` has a null or empty `WorkId`
- **THEN** its projected `workId` equals its `TaskRun.Id`

#### Scenario: active work is the single current running work

- **WHEN** the run is assigned to worker `W` and the current stage has a `Running` task claimed
  by `W` with effective work id `X`
- **THEN** the projection's active-work identity is exactly `workId = X`, `workerId = W`

#### Scenario: no active work

- **WHEN** the run has no `Running` task in its current stage and no active checks
- **THEN** the projection indicates the run has no active work

### Requirement: Reads MUST NOT deserialize WorkflowRun State

Resolving work identity or active-work membership from the projection MUST NOT deserialize
the run's `State` JSON (no full `WorkflowRun` materialization). A projection read SHALL
return its result from the persisted projection alone.

#### Scenario: query without State deserialization

- **WHEN** any caller resolves `taskId → workId`, `workId → taskId`, or active-work membership
  for a run
- **THEN** the `WorkflowRuns.State` column is not deserialized by that resolution

### Requirement: Projection is maintained on every run write

The projection SHALL be updated in the same commit that persists `State` on every
WorkflowRun state change, so the projected mapping and active-work identity always reflect
the `State` just written. The projection is write-maintained: it is recomputed from the
in-memory run at save time, never reconstructed by deserializing `State` on read.

#### Scenario: write keeps projection in lock-step with State

- **WHEN** a run write commits a `State` change (task claimed, task completed, stage advanced,
  recovery task inserted, assignment changed, run terminalized)
- **THEN** the projection committed in the same transaction reflects the new mapping and the
  new active-work identity derived from that same run state

#### Scenario: read after commit observes the new projection

- **WHEN** a projection read follows a committed run write
- **THEN** the read returns the mapping and active-work identity produced by that write

### Requirement: Projection lifecycle follows the run

When a WorkflowRun is deleted, its projection SHALL be removed. When a run does not exist,
every projection read for that run SHALL return no result.

#### Scenario: deleted run has no projection

- **WHEN** a WorkflowRun row is deleted
- **THEN** the projection rows for that run are removed and subsequent projection reads return
  no result

#### Scenario: unknown run resolves to nothing

- **WHEN** a projection read targets a `workflowRunId` that does not exist
- **THEN** the read returns no mapping and no active work, without error
