

### Requirement: rerun-from-stage performs range invalidation on the control plane

The workflow run SHALL provide a `rerun-from-stage` control action that takes a target stage id and invalidates the control state of the target stage and every stage after it, while leaving every stage before the target untouched. The action SHALL be a pure control-plane operation: it SHALL re-initialize stage control state so execution resumes from the target stage, and SHALL NOT roll back, undo, or recreate any execution fact (workspace contents, git state, runtime variables, external side effects). Specifically:

- Stages strictly before the target SHALL remain unchanged and SHALL continue to be the authoritative source of current progress.
- The target stage SHALL be replaced by a new `StageRun` with the same id, `Attempt = old.Attempt + 1`, `Initialized = false`, empty tasks and checks.
- Every stage after the target SHALL be replaced by a fresh `StageRun` with the same id, `Attempt = 1`, `Initialized = false`, empty tasks and checks.
- `CurrentStageId` SHALL become the target stage id.
- The workflow run `Failure` SHALL be cleared and `Status` SHALL become `Running`.
- Stages after the target SHALL be (re)initialized lazily by the existing stage-initialization + advance path as the workflow reaches them, loading the current effective workflow template at that time.

#### Scenario: Target stage becomes a new attempt

- **WHEN** `rerun-from-stage` is invoked on a run whose target stage has `Attempt = 2` and `Initialized = true`
- **THEN** the target stage SHALL be replaced with a `StageRun` whose `Attempt` is `3` and `Initialized` is `false`
- **AND** the target stage's prior tasks and checks SHALL be discarded from control state

#### Scenario: Later stages reset to fresh first attempt

- **WHEN** `rerun-from-stage` is invoked on a run that has already reached a later stage with `Attempt = 1`, `Initialized = true`, and populated tasks
- **THEN** every stage after the target SHALL be replaced with a fresh `StageRun` of `Attempt = 1`, `Initialized = false`, and empty tasks and checks
- **AND** those later stages SHALL NOT be initialized until the workflow advances into them

#### Scenario: Stages before the target are preserved as progress

- **WHEN** `rerun-from-stage` is invoked targeting the `build` stage of a run that has completed `plan`
- **THEN** the `plan` stage's tasks, checks, and completion state SHALL remain unchanged
- **AND** the preserved `plan` results SHALL continue to be treated as the current authoritative progress

#### Scenario: Run state resumes running at the target

- **WHEN** `rerun-from-stage` is invoked on a run whose `Status` is `Failed` with a populated `Failure`
- **THEN** `CurrentStageId` SHALL become the target stage id
- **AND** `Failure` SHALL be cleared
- **AND** `Status` SHALL become `Running`

#### Scenario: Later stages reinitialize against the current template

- **WHEN** a later stage becomes current after a `rerun-from-stage`
- **THEN** it SHALL be initialized by the same stage-initialization path used for normal advance
- **AND** its task and check definitions SHALL be loaded from the currently effective workflow template

### Requirement: Target stage must be one the workflow run has reached

`rerun-from-stage` SHALL accept only a target stage that this workflow run has already reached. A stage is considered reached when the run has initialized it at least once during its lifetime (equivalently, its position is not strictly ahead of the furthest stage the run has progressed into). Invoking the action with an unknown stage id, or with a stage that exists in the workflow definition but has never been reached by this run, SHALL be rejected with an actionable error identifying the eligible (already-reached) stages. This SHALL NOT perform template look-ahead to initialize never-reached stages.

#### Scenario: Reached stage is accepted

- **WHEN** `rerun-from-stage` is invoked targeting a stage the run has already initialized at least once
- **THEN** the operation SHALL proceed with range invalidation

#### Scenario: Never-reached stage is rejected

- **WHEN** `rerun-from-stage` is invoked targeting a stage that exists in the workflow but lies strictly ahead of the furthest stage the run has reached
- **THEN** the operation SHALL be rejected with an actionable error
- **AND** the error SHALL indicate which stages are eligible for selection

#### Scenario: Unknown stage is rejected

- **WHEN** `rerun-from-stage` is invoked with a stage id that is not part of the run's stages
- **THEN** the operation SHALL be rejected with an actionable error

### Requirement: Active work in the invalidation range blocks the operation

`rerun-from-stage` SHALL be a recovery operation that does NOT implicitly stop in-flight execution. Before applying range invalidation, the action SHALL detect any active work within the target-to-end range. Active work is any stage in that range holding a non-terminal task (e.g. a running task) or a pending/running check. When active work is detected, the operation SHALL be rejected and the workflow run state SHALL remain unchanged, with an actionable error instructing the user to first `stop` or `cancel` the active work and then retry. The detection SHALL consider every stage in the invalidation range, not only the target stage.

#### Scenario: Active task in a later stage blocks the operation

- **WHEN** `rerun-from-stage` targets the `plan` stage and a stage after the target has a task in a running state
- **THEN** the operation SHALL be rejected
- **AND** the run's stage list, `CurrentStageId`, `Failure`, and `Status` SHALL remain unchanged
- **AND** the error SHALL instruct the user to `stop` or `cancel` the active work before retrying

#### Scenario: Pending check in the target stage blocks the operation

- **WHEN** the target stage itself has a check that is pending or running
- **THEN** the operation SHALL be rejected with an actionable error
- **AND** no stage control state SHALL be modified

#### Scenario: Clean range proceeds

- **WHEN** every stage in the target-to-end range has only terminal tasks and no pending or running checks
- **THEN** the operation SHALL proceed with range invalidation

### Requirement: Stage locks are released consistently within the range

On a successful `rerun-from-stage`, the workflow grain SHALL release any sequential stage lock it currently holds for stages within the target-to-end range, using the same release semantics as the existing `rerun` action. The operation SHALL NOT release locks for stages before the target. After the operation completes, there SHALL be no residual stage lock owned by this workflow run for any stage in the invalidation range, and SHALL NOT create orphan locks. Lock release SHALL NOT implicitly stop, cancel, or report failure for any active work.

#### Scenario: Lock held on a stage in the range is released

- **WHEN** the workflow run holds a sequential stage lock for a stage within the target-to-end range and `rerun-from-stage` succeeds
- **THEN** that lock SHALL be released with a reason indicating rerun-from-stage
- **AND** the next waiter (if any) SHALL become eligible to acquire it

#### Scenario: Locks before the target are left intact

- **WHEN** `rerun-from-stage` succeeds and a lock for a stage before the target exists
- **THEN** that lock SHALL NOT be released by this operation

### Requirement: Execution facts and external side effects are not rolled back

`rerun-from-stage` SHALL NOT clear, revert, or recreate execution facts or external side effects. Specifically: workflow run runtime variables (the product of `setVars`, scoped to the run and not to any individual attempt) SHALL be preserved; a new attempt's `setVars` SHALL naturally overwrite any same-named key. Workspace contents, git state (branches, commits, pushes), and external side effects already produced (created PRs, merged content, archived OpenSpec changes) SHALL NOT be automatically undone, deleted, or reverted by this action. Because the workspace is not reset, actions reached by `rerun-from-stage` SHALL be required to be reentrant — they MUST produce a consistent result when re-executed on a workspace that already contains their own prior artifacts (for example reusing an existing draft PR rather than creating a duplicate). Reentrancy is an action contract responsibility, not a workflow-engine responsibility.

#### Scenario: Runtime variables survive the operation

- **WHEN** a run has runtime variables written by stages before the target and `rerun-from-stage` is invoked
- **THEN** those runtime variables SHALL remain present on the run after the operation
- **AND** the target stage's new attempt SHALL be able to read them

#### Scenario: New attempt setVars overwrites same-named keys

- **WHEN** the target stage's new attempt writes a runtime variable key that already exists from an earlier stage
- **THEN** the new value SHALL overwrite the prior value for that key

#### Scenario: External side effects are not reverted

- **WHEN** a prior attempt of a reached action has created a pull request, pushed a branch, or archived an OpenSpec change
- **AND** `rerun-from-stage` causes that action to execute again
- **THEN** the system SHALL NOT delete, close, or revert those prior external side effects as part of the operation

#### Scenario: Reached actions must be reentrant

- **WHEN** an action reached by `rerun-from-stage` executes on a workspace that already contains its own prior artifact
- **THEN** the action SHALL produce a consistent result rather than fail or duplicate the artifact
- **AND** the workflow engine SHALL NOT be responsible for making a non-reentrant action safe to re-execute

### Requirement: Invalidated old stage run data is not retained

`rerun-from-stage` SHALL NOT retain the old `StageRun` data of invalidated stages as historical records. `StageRun` is derived control state, not an execution fact; once a stage is invalidated its prior task/check/attempt records SHALL be discarded from the run's stage list (replaced per the range-invalidation requirement). The workflow timeline SHALL NOT present old-attempt history for invalidated stages. Execution facts that live outside `StageRun` (commits, diffs, variables, session logs) are out of scope for this requirement and are governed by their own retention.

#### Scenario: Old stage run is replaced not appended

- **WHEN** a stage is invalidated by `rerun-from-stage`
- **THEN** the run's stage list SHALL contain exactly one entry for that stage id (the new attempt)
- **AND** SHALL NOT retain a separate historical entry for the prior attempt

#### Scenario: Timeline omits invalidated attempt history

- **WHEN** a client reads the workflow timeline after a `rerun-from-stage`
- **THEN** the timeline SHALL NOT surface prior-attempt task/check history for invalidated stages as distinct historical entries

### Requirement: rerun-from-stage HTTP endpoint

The server SHALL provide `POST /api/projects/{projectRef}/issues/{number}/rerun-from-stage` to trigger the range-invalidation recovery action. The request body SHALL be `{ stage: string }` where `stage` is the target stage id and MUST be non-empty. The endpoint SHALL resolve the issue's active workflow run, SHALL be permitted when the workflow is in a failed or otherwise active controllable state (matching the eligibility of the existing `retry`/`rerun` actions), and SHALL delegate to the workflow grain's `RerunFromStage` method. Actionable validation failures (unknown/never-reached stage, active work in range) SHALL be returned via the existing conflict/bad-request error channel with a machine-readable code and a human-readable message; on success the response SHALL be `200`. The endpoint SHALL NOT accept the request when the issue has no workflow run or the workflow is in a non-controllable terminal state.

#### Scenario: Valid request returns 200

- **WHEN** a client sends `POST /api/projects/{projectRef}/issues/{number}/rerun-from-stage` with `{ "stage": "build" }` for an issue whose run has reached `build`
- **AND** no active work exists in the invalidation range
- **THEN** the server SHALL invoke the workflow grain's range-invalidation method
- **AND** the response SHALL be `200`

#### Scenario: Never-reached stage returns an actionable error

- **WHEN** a client sends a rerun-from-stage request targeting a stage the run has not reached
- **THEN** the server SHALL return an actionable error identifying the eligible stages
- **AND** the workflow run state SHALL remain unchanged

#### Scenario: Active work in range returns a conflict

- **WHEN** a client sends a rerun-from-stage request and active work exists in the target-to-end range
- **THEN** the server SHALL return a conflict error instructing the user to `stop` or `cancel` first
- **AND** the workflow run state SHALL remain unchanged

#### Scenario: Empty stage field rejected with 400

- **WHEN** a client sends a rerun-from-stage request with `{ "stage": "" }`, whitespace-only stage, or a missing `stage` field
- **THEN** the server SHALL return `400 Bad Request`

#### Scenario: No workflow run returns 404

- **WHEN** a client sends a rerun-from-stage request for an issue that has no workflow run
- **THEN** the server SHALL return `404 Not Found`

### Requirement: rerun-from-stage CLI command

The `mo` CLI SHALL provide an `issue rerun-from-stage` subcommand parallel to the existing `issue retry` and `issue rerun` subcommands. It SHALL accept the issue number argument and a stage option/argument identifying the target stage, and SHALL issue a `POST` to the `rerun-from-stage` endpoint with `{ stage }`. On an actionable server error the CLI SHALL surface the server-provided message and code. The command SHALL share the project-resolution and output conventions of the other workflow-control subcommands.

#### Scenario: CLI dispatches the recovery action

- **WHEN** a user runs `mo issue rerun-from-stage <number> --stage build` (or equivalent stage argument) for a project
- **THEN** the CLI SHALL resolve the project id
- **AND** SHALL POST `{ "stage": "build" }` to the issue's `rerun-from-stage` endpoint

#### Scenario: Missing stage is rejected by the CLI

- **WHEN** a user runs `mo issue rerun-from-stage <number>` without supplying a target stage
- **THEN** the CLI SHALL report a usage error and SHALL NOT make the request

#### Scenario: CLI surfaces actionable server errors

- **WHEN** the server returns a conflict or bad-request error with a code and message
- **THEN** the CLI SHALL surface that message and code to the user

### Requirement: Existing retry and rerun semantics are unchanged

Introducing `rerun-from-stage` SHALL NOT alter the behavior of the existing `retry` (re-enqueue the currently failed task/check) and `rerun` (re-run the current stage as `rerun-from-stage` against the current stage) actions. The three actions SHALL remain distinct, parallel control actions. `rerun` SHALL continue to be equivalent to `rerun-from-stage` scoped to the current stage only; this issue SHALL NOT merge the three actions into one.

#### Scenario: retry behavior unchanged

- **WHEN** a user invokes `retry` on a failed task
- **THEN** the engine SHALL re-enqueue only the currently failed task/check
- **AND** SHALL NOT perform range invalidation across stages

#### Scenario: rerun behavior unchanged

- **WHEN** a user invokes `rerun`
- **THEN** the engine SHALL re-run only the current stage (producing a new attempt of the current stage)
- **AND** SHALL NOT invalidate any other stage
