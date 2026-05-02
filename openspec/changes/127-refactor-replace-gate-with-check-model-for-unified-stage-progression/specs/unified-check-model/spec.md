## ADDED Requirements

### Requirement: BaseStageRunner provides unified execution loop

All stages SHALL execute through a single `BaseStageRunner` abstract class that implements the execution loop: Tasks → Checks → Reactions. Concrete stage runners extend `BaseStageRunner` and declare their Task list, Check list, and next stage.

#### Scenario: Plan stage uses BaseStageRunner

- **WHEN** Plan stage executes
- **THEN** `BaseStageRunner.run()` executes Plan's Task list (Generate Proposal, Generate Specs, Generate Design, Generate Tasks, Self-Review)
- **AND** after tasks complete, runs Plan's Check list (proposal-complete, specs-complete, design-complete, tasks-valid, self-review-passed, user-approval)
- **AND** if all checks pass, advances to Build stage
- **AND** if any check fails, triggers that check's Reaction

#### Scenario: Build stage uses BaseStageRunner

- **WHEN** Build stage executes
- **THEN** `BaseStageRunner.run()` executes Build's Task list (Execute Tasks in DAG order)
- **AND** after tasks complete, runs Build's Check list (all-tasks-complete, code-compiles)
- **AND** if all checks pass, advances to Check stage
- **AND** if any check fails, triggers that check's Reaction

#### Scenario: Check stage uses BaseStageRunner

- **WHEN** Check stage executes
- **THEN** `BaseStageRunner.run()` executes Check's Task list (Run Build-Test, Run AI Review)
- **AND** after tasks complete, runs Check's Check list (build-test-passed, ai-review-passed, user-approval)
- **AND** if all checks pass, advances to Done stage
- **AND** if any check fails, triggers that check's Reaction

#### Scenario: Done stage uses BaseStageRunner

- **WHEN** Done stage executes
- **THEN** `BaseStageRunner.run()` executes Done's Task list (rebase onto master, run build verification, fast-forward merge, delete worktree)
- **AND** after tasks complete, runs Done's Check list (merge-successful, build-verify-passed)
- **AND** if all checks pass, Issue is closed
- **AND** if any check fails, triggers that check's Reaction

### Requirement: Check interface with Reaction

Each Check SHALL define a `name`, a `run()` method, and a `reaction` strategy that specifies what happens when the check fails.

**Reaction strategies:**
- `retry-task`: Re-execute the stage's task list from the point of failure
- `auto-fix`: AI agent attempts to fix the issue automatically, with a configurable max retry count
- `escalate`: Transition the issue to a prior stage (e.g., CHECK → BUILD, CHECK → PLAN, BUILD → PLAN)
- `ask-user`: Pause pipeline and wait for user input (used exclusively by `user-approval` check)

#### Scenario: Check with retry-task reaction

- **WHEN** a check named `self-review-passed` fails
- **AND** its reaction is `retry-task`
- **THEN** the stage re-executes its task list from the beginning
- **AND** the retry count for this check is incremented
- **AND** if retry count exceeds the configured maximum (default 3), the check's reaction upgrades to `escalate`

#### Scenario: Check with auto-fix reaction

- **WHEN** a check named `build-test-passed` fails
- **AND** its reaction is `auto-fix`
- **THEN** an AI agent attempts to fix the code that caused the test failure
- **AND** after the fix attempt, the check re-runs
- **AND** if the check still fails after max auto-fix attempts (configurable, default 2), the reaction escalates

#### Scenario: Check with escalate reaction

- **WHEN** a check named `ai-review-passed` fails
- **AND** its reaction is `escalate` with target `plan`
- **THEN** the issue transitions to the `plan` stage
- **AND** the pipeline re-enters the plan stage's execution loop
- **AND** the failure context is passed to the plan stage as input

#### Scenario: Check with ask-user reaction

- **WHEN** a check named `user-approval` fails (user has not yet approved or has rejected)
- **AND** its reaction is `ask-user`
- **THEN** the pipeline pauses
- **AND** the system emits an `approval_requested` event
- **AND** the pipeline waits for user action (approve or reject with feedback)
- **AND** on approval, the check passes and the pipeline continues
- **AND** on rejection, the check triggers its fallback reaction (`escalate` to the appropriate prior stage)

### Requirement: user-approval is a Check, not a Gate

`user-approval` SHALL be implemented as a standard Check item in the checks list of stages that require human approval. The system SHALL NOT have a separate `gate`, `gate_after`, `requiresApproval`, or `gateRequired` concept.

#### Scenario: Plan stage includes user-approval check

- **WHEN** Plan stage runs its checks
- **THEN** the `user-approval` check evaluates whether the user has approved the plan
- **AND** if not yet approved, the check returns `pending` and triggers `ask-user` reaction
- **AND** on user approval, the check returns `pass`

#### Scenario: Check stage includes user-approval check

- **WHEN** Check stage runs its checks
- **THEN** the `user-approval` check evaluates whether the user has approved the implementation
- **AND** if not yet approved, the check returns `pending` and triggers `ask-user` reaction
- **AND** on user approval, the check returns `pass`

#### Scenario: Build stage has no user-approval check

- **WHEN** Build stage runs its checks
- **THEN** the check list does not include `user-approval`
- **AND** if all checks pass, the stage automatically advances to Check without pausing

### Requirement: Check evaluation is serial and blocking

All checks within a stage SHALL execute serially, one at a time. No parallel check execution. When a check fails with a reaction other than `ask-user`, the remaining checks SHALL NOT run.

#### Scenario: Serial check execution with early failure

- **WHEN** Plan stage runs checks [proposal-complete, specs-complete, design-complete]
- **AND** `specs-complete` fails
- **THEN** `design-complete` and subsequent checks are NOT executed
- **AND** the `specs-complete` reaction is triggered

#### Scenario: All checks pass sequentially

- **WHEN** Plan stage runs checks [proposal-complete, specs-complete, design-complete, tasks-valid, self-review-passed, user-approval]
- **AND** each check passes in sequence
- **THEN** the stage advances to Build

### Requirement: StageRunResult has no gate fields

`StageRunResult` SHALL contain `success`, `nextStage`, `checkResults`, and optional `message`. It SHALL NOT contain `requiresApproval` or `gateRequired` fields. `PipelineResult` SHALL NOT contain `gateRequired`.

#### Scenario: Successful stage result

- **WHEN** a stage completes with all checks passing
- **THEN** `StageRunResult` contains `{ success: true, nextStage: <next>, checkResults: [...] }`
- **AND** does not contain a `requiresApproval` field

#### Scenario: Failed stage result with escalation

- **WHEN** a check fails with `escalate` reaction
- **THEN** `StageRunResult` contains `{ success: false, escalateToStage: <target>, checkResults: [...], message: <reason> }`

### Requirement: All stages persist execution state uniformly

Every stage (Plan, Build, Check, Done) SHALL persist task execution records and check results to the same data store. No stage SHALL have its own isolated persistence mechanism.

#### Scenario: Plan stage persists check results

- **WHEN** Plan stage completes its checks
- **THEN** check results are persisted with the issue ID and stage name
- **AND** the data is queryable alongside Build and Check stage results

#### Scenario: Build stage persists check results

- **WHEN** Build stage completes its checks
- **THEN** check results are persisted with the issue ID and stage name
- **AND** uses the same schema as Plan and Check stage results

### Requirement: WorkflowEngine executes checks and advances without gate logic

`WorkflowEngine.run()` SHALL execute each stage's `BaseStageRunner.run()`, examine the result, and advance to the next stage on success. It SHALL NOT contain approval gate logic, `requiresApproval` branching, or `setApprovalState` calls.

#### Scenario: Engine advances on all checks pass

- **WHEN** a stage runner returns `{ success: true, nextStage: Stage.Build }`
- **THEN** the engine updates the issue stage to `Build`
- **AND** continues the pipeline loop

#### Scenario: Engine handles escalation

- **WHEN** a stage runner returns `{ success: false, escalateToStage: Stage.Plan }`
- **THEN** the engine updates the issue stage to `Plan`
- **AND** continues the pipeline loop from Plan
