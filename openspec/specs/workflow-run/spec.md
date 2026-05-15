# OpenSpec Capability: workflow-run

### Requirement: REQ-WR-002 Build tasks materialize into the WorkflowRun

After Plan produces and validates `tasks.json`, Build tasks SHALL be materialized as TaskRun instances under the same WorkflowRun's Build StageRun. `tasks.json` MAY remain the design artifact and Build input, but runtime task progress, skipped/completed/failed state, attempts, artifacts, output, and failure evidence SHALL be stored in WorkflowRun tasks.

#### Scenario: Tasks file becomes Build task instances

- **WHEN** Plan has produced a valid `tasks.json`
- **THEN** the system SHALL create or update Build StageRun task instances in the active WorkflowRun for each task in the file
- **AND** repeated materialization SHALL NOT create duplicate task rows for the same task id

#### Scenario: Build execution updates WorkflowRun tasks

- **WHEN** Ralph executes, skips, completes, or fails a Build task
- **THEN** the corresponding WorkflowRun task SHALL reflect the latest status, attempts, artifacts, and output
- **AND** the primary user-facing Build task list SHALL NOT be reconstructed from `tasks.json`, logs, checkpoints, or session events

### Requirement: REQ-WR-004 Evidence and checkpoints remain separate from WorkflowRun state

WorkflowRun SHALL be the current runtime state root and consistency boundary. `stage_executions`, `workflow_log`, session logs, check suites, `stage_states`, and checkpoints SHALL retain evidence, audit, compatibility projection, or resume-cursor roles and SHALL NOT be used as the primary source for current stage, task, check, approval, or failure decisions.

#### Scenario: Logs and projections are evidence only

- **WHEN** the UI, API, or recovery logic needs current stage, task, check, approval, or failure state
- **THEN** it SHALL read WorkflowRun state when a WorkflowRun exists
- **AND** it SHALL NOT reconstruct that current state from logs, `stage_executions`, check suites, `stage_states`, or checkpoints

#### Scenario: Checkpoint is resume cursor only

- **WHEN** the workflow resumes after interruption
- **THEN** checkpoint data MAY determine the safe external resume point
- **AND** checkpoint data SHALL NOT replace WorkflowRun current stage, task, or check state

### Requirement: REQ-WR-005 Integrate runtime work is first-class WorkflowRun state

Integrate stage progress SHALL be represented in WorkflowRun using standard task and check entities. The Integrate StageRun SHALL expose ordered tasks `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge`, plus check `health:integrate`; merge delivery metadata and post-merge freeze state SHALL be persisted as WorkflowRun facts.

#### Scenario: Integrate stage is seeded with visible work

- **WHEN** an issue starts or resumes with an active WorkflowRun
- **THEN** the Integrate StageRun SHALL contain pending tasks `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` in execution order
- **AND** it SHALL contain a pending check `health:integrate`

#### Scenario: Integrate merge records delivery facts

- **WHEN** `integrate:merge` completes successfully
- **THEN** the task result SHALL record `targetBranch`, `baseSha`, `candidateHeadSha`, `landedSha`, and `rebased` when available
- **AND** the Integrate StageRun SHALL record a freeze point that prevents later automatic code-modifying tasks

#### Scenario: Post-merge health failure is non-repairable

- **WHEN** `health:integrate` fails after `integrate:merge` has completed
- **THEN** WorkflowRun SHALL fail with reason `post-merge-health-failed`
- **AND** it SHALL NOT schedule `fix-integrate-health` regardless of check failure policy configuration

### Requirement: REQ-WR-003 Runtime-added work is represented as normal tasks

Runtime-added repair, rebase, retry, rerun, and conflict-resolution work SHALL be appended to the current StageRun as ordinary WorkflowRun tasks. User-triggered rebase SHALL use a visible `rebase-branch` task in the current stage instead of a hidden queue-only execution path.

#### Scenario: User-triggered rebase appears as current stage work

- **WHEN** a user triggers rebase for a non-Done issue with an active WorkflowRun
- **THEN** the system SHALL append `rebase-branch` to the current StageRun task list with title `Rebase branch`
- **AND** the task SHALL carry reason and causedBy metadata explaining why it was added
- **AND** if a `rebase-branch` task in `pending` or `running` state already exists in that StageRun, the system SHALL NOT append a duplicate task

#### Scenario: Approval-paused stage can execute appended rebase task

- **WHEN** the current StageRun is awaiting approval
- **AND** the system appends `rebase-branch` as new executable work
- **THEN** the StageRun SHALL return to `running` so `nextWork()` can schedule the task
- **AND** prior approval state SHALL remain evidence until later invalidation policy decides whether it is still valid

### Requirement: check-review-repair-policy

WorkflowRun SHALL be the authoritative source for Check `review-passed` repair policy, including the `fix-review-findings` task id and maximum automatic repair attempts. CheckStageRunner and retry paths SHALL NOT expose or apply a conflicting repair attempt budget for the same Check review gate.

#### Scenario: Failed review schedules repair within budget

- **WHEN** Check stage `review-passed` fails
- **AND** the authoritative repair policy still has remaining attempts
- **THEN** WorkflowRun SHALL schedule or expose `fix-review-findings` as the repair task
- **AND** the repair task SHALL be counted against the authoritative repair budget

#### Scenario: Failed review stops when budget is exhausted

- **WHEN** Check stage `review-passed` fails
- **AND** the authoritative repair budget is exhausted
- **THEN** WorkflowRun SHALL fail the Check stage without scheduling another automatic `fix-review-findings` task
- **AND** the failure SHALL remain traceable to the failed `review-passed` gate

#### Scenario: Retry does not imply another repair

- **WHEN** a failed Check review is retried after the repair budget is exhausted
- **THEN** WorkflowRun MAY reset review/checkpoint work needed for checkpoint recovery
- **AND** it SHALL NOT append another `fix-review-findings` task solely because retry was requested

### Requirement: Persist merge-ready evidence for approval and diagnostics

Workflow run records SHALL preserve structured merge-ready output so approval, Integrate, API, CLI, logs, and UI surfaces can display and compare the mergeability evidence used for decisions.

#### Scenario: Check records merge-ready snapshot

- **GIVEN** Check runs the `merge-ready` gate
- **WHEN** the gate completes
- **THEN** the workflow run evidence SHALL include the structured mergeability snapshot with `targetBranch`, `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `strategy`, `canMerge`, and `conflictFiles`

#### Scenario: Integrate records refreshed preflight diagnostics

- **GIVEN** Integrate runs a fresh mergeability preflight because approved evidence is missing or stale
- **WHEN** the preflight completes
- **THEN** Integrate SHALL record diagnostic output containing the current mergeability snapshot
- **AND** refreshed Integrate diagnostics SHALL NOT silently replace the Check approval evidence as user-approved

### Requirement: REQ-WR-001 Starting an issue creates a WorkflowRun

Starting an issue SHALL create or reuse one active WorkflowRun aggregate bound to the issue id and issue number only after the Issue is start eligible. The aggregate SHALL derive its first stage from its ordered stage definition, create ordered StageRuns for `plan`, `build`, `check`, and `integrate`, seed static Plan and Integrate task/check state, and start the first StageRun without using `Issue.stage` as the state-machine decision source.

#### Scenario: Start creates aggregate-rooted run

- **WHEN** an issue is started
- **AND** the issue is start eligible
- **THEN** the system SHALL create or reuse one active WorkflowRun for that issue
- **AND** the WorkflowRun SHALL have `status = running` and `currentStage` equal to the first configured runnable stage
- **AND** the first StageRun SHALL be running
- **AND** issue stage/status updates SHALL be projections of the WorkflowRun decision

#### Scenario: Start is idempotent for active run

- **WHEN** start or resume code encounters an issue that already has a non-terminal active WorkflowRun
- **THEN** it SHALL reuse that WorkflowRun
- **AND** it SHALL NOT create a duplicate active run for the same issue

#### Scenario: Waiting prerequisite prevents WorkflowRun creation

- **WHEN** start-pipeline execution evaluates Issue #201
- **AND** Issue #201 is waiting for prerequisite Issue #200 to be delivered
- **THEN** the system SHALL NOT create or start a WorkflowRun for Issue #201
- **AND** the system SHALL NOT create an agent session for Issue #201
- **AND** the waiting condition SHALL be recorded as start eligibility state rather than workflow failure

### Requirement: retryable-current-stage-rejection

WorkflowRun SHALL expose current-stage retryability using the same semantics as `retryStage(stage)` without mutating or persisting state during retryability evaluation. Approval rejection of the current stage SHALL remain a retryable failed-run state when `retryStage(stage)` accepts the same stage.

#### Scenario: Failed current stage is retryable
- **GIVEN** the latest WorkflowRun has `status=failed`
- **AND** its `currentStage` equals the issue's current stage
- **AND** the current StageRun has `status=failed` because approval was rejected
- **WHEN** resume-pipeline evaluates retryability for that stage
- **THEN** the run SHALL be considered retryable
- **AND** no WorkflowRun state SHALL be changed by the evaluation

#### Scenario: Non-current stage is not retryable
- **GIVEN** the latest WorkflowRun has `status=failed`
- **AND** its `currentStage` differs from the issue's current stage
- **WHEN** resume-pipeline evaluates retryability for the issue stage
- **THEN** the run SHALL NOT be considered retryable
- **AND** the retry SHALL NOT be started

#### Scenario: Non-failed run is not retryable
- **GIVEN** the latest WorkflowRun has a status other than `failed`
- **WHEN** resume-pipeline evaluates retryability for the issue stage
- **THEN** the run SHALL NOT be considered a retryable current-stage failure

### Requirement: stage-approval-rejection-feedback

Rejecting a stage approval SHALL persist the user's rejection feedback in WorkflowRun history as rejection response data. Existing approval request context MAY be retained for audit, but it SHALL NOT replace or hide the user's rejection feedback.

#### Scenario: Rejection message is recorded
- **GIVEN** an issue is awaiting current-stage approval
- **WHEN** the user rejects the approval with feedback text
- **THEN** the WorkflowRun rejected approval state SHALL include that feedback
- **AND** the WorkflowRun failure evidence SHALL remain traceable to `approval-rejected`

#### Scenario: Prior approval context does not shadow feedback
- **GIVEN** an awaiting approval already has approval request output
- **WHEN** the user rejects the approval with different feedback
- **THEN** the persisted rejection response SHALL expose the user's feedback
- **AND** the prior approval request output MAY only appear as separate context

### Requirement: REQ-BDA-REBASE-001 Drift-driven rebase uses visible WorkflowRun tasks

WorkflowRun SHALL schedule drift-driven rebase work only as a visible `rebase-branch` task in the current stage and SHALL deduplicate pending or running rebase tasks.

#### Scenario: Safe window enqueues rebase task

- **WHEN** a drifted issue reaches a safe rebase window
- **AND** policy chooses automatic scheduling
- **THEN** WorkflowRun SHALL append `rebase-branch` to the current StageRun
- **AND** the task SHALL include caused-by metadata explaining base drift

#### Scenario: Pending rebase is not duplicated

- **WHEN** a drifted issue already has a pending or running `rebase-branch` task
- **THEN** WorkflowRun SHALL NOT append another `rebase-branch` task

#### Scenario: Approval-paused stage reopens for rebase work

- **WHEN** a drifted issue is awaiting approval
- **AND** policy schedules `rebase-branch`
- **THEN** the StageRun SHALL return to executable work state
- **AND** the rebase SHALL appear in the normal task list before later checks or approvals continue

### Requirement: Check full verification evidence

The Check StageRun SHALL include a first-class full verification check before review and mergeability checks. The verification check SHALL be persisted as `health:check` or a compatible stable check name and SHALL carry evidence for the candidate implementation it verified.

#### Scenario: Check stage is seeded with verification check

- **WHEN** a WorkflowRun creates or materializes the Check StageRun
- **THEN** the Check StageRun SHALL include `health:check` before `review-passed` and `merge-ready`
- **AND** `health:check` SHALL be visible as normal StageRun check state

#### Scenario: Passing verification evidence is persisted

- **WHEN** Check full verification passes
- **THEN** the Check StageRun SHALL persist a passing check result for `health:check`
- **AND** the result SHALL include command, status, duration, summary or message, and candidate snapshot metadata

#### Scenario: Failing verification evidence is persisted

- **WHEN** Check full verification fails or times out
- **THEN** the Check StageRun SHALL persist a failed check result for `health:check`
- **AND** the result SHALL include command, status, duration, summary, and a useful bounded log excerpt
- **AND** later Check approval evidence SHALL NOT be created for that failed candidate

### Requirement: Check candidate evidence invalidation

Candidate-changing Check work SHALL invalidate verification evidence together with review, merge-ready, and approval evidence.

#### Scenario: Candidate change invalidates Check evidence

- **WHEN** Check-stage work changes the candidate implementation after `health:check` has passed
- **THEN** the system SHALL invalidate or reset `health:check`
- **AND** it SHALL invalidate or reset dependent review, merge-ready, and approval state for the old candidate

