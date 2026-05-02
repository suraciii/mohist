## ADDED Requirements

### Requirement: StageTask unified type definition

The system SHALL define a `StageTask` interface as the unified Task type across all stages (Plan, Build, Check). Every execution unit in any stage — Plan rounds, Build tasks from tasks.json, Check review rounds — SHALL be representable as a `StageTask`.

```typescript
interface StageTask {
  id: string
  title: string
  status: 'pending' | 'running' | 'completed' | 'failed'
  order: number
  dependsOn: string[]
  source: 'static' | 'dynamic'
  artifacts: string[]
  attempts: number
  maxAttempts: number
  startedAt?: string
  completedAt?: string
}
```

- `id`: unique within the stage — `proposal` / `T-001` / `review`
- `source`: `static` for fixed tasks (Plan's 5 rounds, Check's 2 rounds); `dynamic` for tasks loaded from tasks.json (Build)
- `dependsOn`: empty array for linear execution, populated for DAG-based execution (Build tasks)
- `artifacts`: output file paths produced by this task

#### Scenario: Plan stage tasks are static

- **WHEN** Plan stage starts
- **THEN** the system defines 5 StageTask entries with `source: 'static'`, sequential `order` values 0–4, empty `dependsOn`, and ids: `proposal`, `specs`, `design`, `tasks`, `self-review`
- **AND** `artifacts` for each task points to the corresponding output file path

#### Scenario: Build stage tasks are dynamic

- **WHEN** Build stage starts and reads tasks.json containing T-001, T-002, T-003
- **THEN** the system creates StageTask entries with `source: 'dynamic'`, `dependsOn` populated from tasks.json dependencies
- **AND** `artifacts` is initially empty (filled after execution)

#### Scenario: Check stage tasks are static

- **WHEN** Check stage starts
- **THEN** the system defines 2 StageTask entries with `source: 'static'`, sequential `order` values 0–1, empty `dependsOn`, and ids: `review`, `review-self-check`

### Requirement: StageTaskResult records individual task outcomes

The system SHALL define a `StageTaskResult` interface that records the outcome of a single Task execution. Each task completion SHALL produce one `StageTaskResult` record.

```typescript
interface StageTaskResult {
  taskId: string
  title: string
  status: 'completed' | 'failed' | 'skipped'
  artifacts: string[]
  attempts: number
  duration: number
}
```

- `duration`: wall-clock time in milliseconds from task start to completion
- `attempts`: number of execution attempts (including retries)
- `skipped`: task was skipped due to checkpoint resume

#### Scenario: Plan proposal task completes successfully

- **WHEN** the `proposal` task in Plan stage completes successfully on the first attempt after 45 seconds
- **THEN** a `StageTaskResult` is created with `{ taskId: 'proposal', status: 'completed', attempts: 1, duration: 45000, artifacts: ['proposal.md'] }`

#### Scenario: Build task fails after 3 retries

- **WHEN** task T-002 fails after 3 execution attempts
- **THEN** a `StageTaskResult` is created with `{ taskId: 'T-002', status: 'failed', attempts: 3, artifacts: [] }`

#### Scenario: Check review task skipped on resume

- **WHEN** Check stage resumes from checkpoint and the `review` task artifact already exists
- **THEN** a `StageTaskResult` is created with `{ taskId: 'review', status: 'skipped', attempts: 0, artifacts: ['review.md'] }`

### Requirement: TaskConfig replaces RoundConfig

Plan stage runner and Check stage runner SHALL rename the internal `RoundConfig` interface to `TaskConfig`. The `type` field maps to `StageTask.id`, `label` maps to `StageTask.title`, `outputPath` maps to `StageTask.artifacts`, and array index maps to `StageTask.order`. The internal execution logic (ACP shared connection, retry, checkpoint) SHALL remain unchanged.

#### Scenario: Plan stage uses TaskConfig

- **WHEN** the Plan stage runner source is inspected
- **THEN** no `RoundConfig` interface or variable named `rounds` exists
- **AND** a `TaskConfig` interface exists with fields mapping to StageTask
- **AND** the internal array is named `tasks` (or `taskConfigs`)

#### Scenario: Check stage uses TaskConfig

- **WHEN** the Check stage runner source is inspected
- **THEN** no `RoundConfig` interface exists
- **AND** a `TaskConfig` interface exists

### Requirement: BaseStageRunner persists structured task results

`BaseStageRunner.persistTaskResults` SHALL write `StageTaskResult[]` to `stage_executions.task_results` instead of the raw `unknown` return value from `executeTasks()`. Each stage runner SHALL accumulate `StageTaskResult` entries as individual tasks complete and pass the full array to `persistTaskResults`.

#### Scenario: Plan stage writes per-task results

- **WHEN** Plan stage completes all 5 tasks (proposal, specs, design, tasks, self-review)
- **THEN** `stage_executions.task_results` contains a `StageTaskResult[]` with 5 entries
- **AND** each entry has `taskId`, `status`, `duration`, `attempts`, and `artifacts` fields

#### Scenario: Build stage writes per-task results

- **WHEN** Build stage completes with 3 tasks (T-001 completed, T-002 failed, T-003 skipped)
- **THEN** `stage_executions.task_results` contains a `StageTaskResult[]` with 3 entries
- **AND** entries reflect individual task outcomes

#### Scenario: Check stage writes per-task results

- **WHEN** Check stage completes its 2 tasks (review, review-self-check)
- **THEN** `stage_executions.task_results` contains a `StageTaskResult[]` with 2 entries

### Requirement: StageTaskResult written incrementally

Stage runners SHALL write each `StageTaskResult` to `stage_executions.task_results` as soon as the individual task completes, not only at the end of `executeTasks()`. The `task_results` column SHALL accumulate entries over time.

#### Scenario: Plan task result written immediately on completion

- **WHEN** the `proposal` task in Plan stage completes
- **THEN** `stage_executions.task_results` is updated to include the `proposal` StageTaskResult
- **AND** when `specs` task starts, `task_results` already contains the `proposal` entry

#### Scenario: Build task result written on each task completion

- **WHEN** Build task T-001 completes
- **THEN** `stage_executions.task_results` includes T-001's StageTaskResult
- **WHEN** Build task T-002 then completes
- **THEN** `stage_executions.task_results` includes both T-001 and T-002 results

### Requirement: StageExecutionRepo findByIssueId method

`StageExecutionRepo` SHALL expose a `findByIssueId(issueId: string)` method that returns all `StageExecution` records for a given issue, ordered by `created_at ASC`. This supports the API endpoint and frontend queries.

#### Scenario: Multiple executions for an issue with escalation

- **WHEN** issue #5 has a Plan execution (passed), a Build execution (failed), and another Build execution (passed, after retry)
- **THEN** `findByIssueId('issue-5-uuid')` returns 3 records ordered by `created_at`
- **AND** each record includes `taskResults` as `StageTaskResult[]`

#### Scenario: No executions for a draft issue

- **WHEN** `findByIssueId` is called for an issue that has never been started
- **THEN** an empty array is returned
