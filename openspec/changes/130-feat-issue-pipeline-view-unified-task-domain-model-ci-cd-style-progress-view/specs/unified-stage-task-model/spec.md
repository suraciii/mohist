## ADDED Requirements

### Requirement: StageTask unified domain type

The system SHALL define a `StageTask` interface as the canonical type for all stage work units across Plan, Build, and Check stages. Every unit of work an Agent performs within a stage SHALL be represented as a `StageTask`.

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

- `id`: Unique identifier within the stage. Plan uses round type (e.g., `proposal`, `specs`). Build uses task ID from tasks.json (e.g., `T-001`). Check uses round type (e.g., `review`).
- `source`: `'static'` for Plan (fixed 5 tasks) and Check (fixed 2 tasks); `'dynamic'` for Build (from tasks.json).
- `dependsOn`: Empty array for linear execution (Plan, Check). May contain IDs for DAG-based execution (Build).
- `artifacts`: Output file paths produced by this task.

#### Scenario: Plan stage exposes 5 static StageTasks

- **WHEN** PlanStageRunner begins execution
- **THEN** the stage exposes 5 StageTasks with ids: `proposal`, `specs`, `design`, `tasks`, `self-review`
- **AND** each has `source: 'static'`, `dependsOn: []`, and `order` matching array index (0-4)
- **AND** `artifacts` contains the expected output file paths (e.g., `proposal` task has `['proposal.md']`)

#### Scenario: Build stage exposes dynamic StageTasks from tasks.json

- **WHEN** Build stage reads tasks.json with 3 tasks (T-001, T-002, T-003)
- **THEN** the stage exposes 3 StageTasks with ids from tasks.json
- **AND** each has `source: 'dynamic'` and `dependsOn` from the tasks.json dependency graph
- **AND** `order` reflects topological sort of the DAG

#### Scenario: Check stage exposes 2 static StageTasks

- **WHEN** CheckStageRunner begins execution
- **THEN** the stage exposes 2 StageTasks with ids: `review`, `review-self-check`
- **AND** each has `source: 'static'`, `dependsOn: []`, and `order` matching array index (0-1)

### Requirement: StageTaskResult records individual task outcomes

The system SHALL define a `StageTaskResult` interface that records the outcome of a single task execution. Each task completion SHALL produce one `StageTaskResult` record.

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

- `duration`: Execution time in milliseconds.
- `attempts`: Total number of execution attempts (including retries).
- `artifacts`: File paths verified to exist after task completion.

#### Scenario: Plan proposal task completes on first attempt

- **WHEN** the Plan `proposal` task completes successfully after one execution
- **THEN** a StageTaskResult is recorded with `taskId: 'proposal'`, `status: 'completed'`, `attempts: 1`, `duration` > 0, and `artifacts: ['proposal.md']`

#### Scenario: Build task T-002 fails after 3 attempts

- **WHEN** Build task T-002 fails after 3 retry attempts
- **THEN** a StageTaskResult is recorded with `taskId: 'T-002'`, `status: 'failed'`, `attempts: 3`

#### Scenario: Check review task is skipped due to checkpoint

- **WHEN** the Check `review` task is skipped because its artifact already exists and is in the checkpoint
- **THEN** a StageTaskResult is recorded with `taskId: 'review'`, `status: 'skipped'`, `attempts: 0`

### Requirement: Plan RoundConfig maps to StageTask

PlanStageRunner's internal `RoundConfig` SHALL be mapped to `StageTask` for external consumption. The mapping SHALL be: `type` → `id`, `label` → `title`, array index → `order`, `outputPath` → `artifacts[0]`, `source: 'static'`.

#### Scenario: RoundConfig 'proposal' maps to StageTask

- **WHEN** PlanStageRunner defines a RoundConfig with `type: 'proposal'`, `label: 'proposal.md'`, `outputPath: '<changeDir>/proposal.md'` at index 0
- **THEN** the corresponding StageTask has `id: 'proposal'`, `title: 'proposal.md'`, `order: 0`, `source: 'static'`, `artifacts: ['<changeDir>/proposal.md']`

### Requirement: Check RoundConfig maps to StageTask

CheckStageRunner's internal `RoundConfig` SHALL be mapped to `StageTask` using the same mapping as PlanStageRunner.

#### Scenario: RoundConfig 'review' maps to StageTask

- **WHEN** CheckStageRunner defines a RoundConfig with `type: 'review'`, `label: 'review'`, `outputPath: '<changeDir>/review.md'` at index 0
- **THEN** the corresponding StageTask has `id: 'review'`, `title: 'review'`, `order: 0`, `source: 'static'`

### Requirement: stage_executions.task_results stores StageTaskResult array

`stage_executions.task_results` column SHALL store `StageTaskResult[]` instead of opaque `unknown` JSON. Each task SHALL append its result to the array upon completion. The `StageExecutionRepo.taskResults` type SHALL be narrowed from `unknown[]` to `StageTaskResult[]`.

#### Scenario: Plan stage writes 5 StageTaskResults

- **WHEN** Plan stage completes all 5 tasks successfully
- **THEN** `stage_executions.task_results` contains a JSON array of 5 StageTaskResult objects
- **AND** each has a unique `taskId` matching the task id (`proposal`, `specs`, `design`, `tasks`, `self-review`)

#### Scenario: Query individual task result by taskId

- **WHEN** the API reads stage_executions for a completed Plan stage
- **THEN** a specific task result can be found by filtering `taskResults` array on `taskId`
- **AND** the result includes `duration`, `attempts`, and `artifacts` for that task

#### Scenario: Partial results after mid-stage failure

- **WHEN** Plan stage fails during the `design` task (index 2)
- **THEN** `stage_executions.task_results` contains 2 StageTaskResult objects (for `proposal` and `specs`)
- **AND** `stage_executions.status` is `'failed'`

### Requirement: BaseStageRunner writes StageTaskResult incrementally

`BaseStageRunner` SHALL provide a protected helper method `recordTaskResult(ctx, result)` that appends a `StageTaskResult` to the current `stage_execution.task_results`. Each stage runner SHALL call this method after each individual task completes, rather than writing a bulk result at the end.

#### Scenario: PlanStageRunner records task result after each round

- **WHEN** PlanStageRunner completes the `proposal` task
- **THEN** `recordTaskResult` is called with `{ taskId: 'proposal', status: 'completed', ... }`
- **AND** the stage_execution's task_results array immediately contains this entry
- **WHEN** PlanStageRunner then completes the `specs` task
- **THEN** task_results array contains both `proposal` and `specs` entries

#### Scenario: BuildStageRunner records task result after each Build task

- **WHEN** RalphExecutor completes task T-001
- **THEN** `recordTaskResult` is called with `{ taskId: 'T-001', status: 'completed', ... }`
- **AND** subsequent T-002 execution can see T-001's result in the array

### Requirement: StageExecutionRepo exposes findByIssueId

`StageExecutionRepo` SHALL provide a `findByIssueId(issueId: string): StageExecution[]` method that returns all stage execution records for a given issue, ordered by `created_at ASC`. This enables the API to return full execution history including escalation retries.

#### Scenario: Issue with Plan→Build→Check has 3 execution records

- **WHEN** issue #1 completes the full pipeline (Plan, Build, Check)
- **THEN** `findByIssueId` returns 3 StageExecution records
- **AND** they are ordered: Plan (oldest), Build, Check (newest)

#### Scenario: Issue with escalation has multiple Plan records

- **WHEN** issue #1 has a cycle: Plan → Build → Check(fail) → Plan(retry) → Build → Check(pass)
- **THEN** `findByIssueId` returns 5+ StageExecution records
- **AND** includes both the initial and retry Plan executions
