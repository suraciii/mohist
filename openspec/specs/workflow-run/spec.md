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

Integrate stage progress SHALL be represented in WorkflowRun using standard task and check entities. The Integrate StageRun SHALL expose ordered tasks `integrate:spec-sync`, `integrate:archive-change`, `integrate:prepare`, and `integrate:publish`, plus check `health:integrate`; delivery metadata (prepared base, published commit, push ownership) and post-publish freeze state SHALL be persisted as WorkflowRun facts.

#### Scenario: Integrate stage is seeded with visible work

- **WHEN** an issue starts or resumes with an active WorkflowRun
- **THEN** the Integrate StageRun SHALL contain pending tasks `integrate:spec-sync`, `integrate:archive-change`, `integrate:prepare`, and `integrate:publish` in execution order
- **AND** it SHALL contain a pending check `health:integrate`

#### Scenario: Integrate prepare records reconciliation facts

- **WHEN** `integrate:prepare` completes successfully
- **THEN** the task result SHALL record `targetBranch`, the base commit it prepared against, the prepared candidate head, and `rebased` when available
- **AND** later Integrate work SHALL treat the issue branch as up to date with that base

#### Scenario: Integrate publish records delivery facts and freezes

- **WHEN** `integrate:publish` completes successfully
- **THEN** the task result SHALL record `targetBranch`, `baseSha`, the landed commit sha, and that the change was pushed to the remote
- **AND** the Integrate StageRun SHALL record a freeze point that prevents later automatic code-modifying tasks

#### Scenario: Post-publish health failure is non-repairable

- **WHEN** `health:integrate` fails after `integrate:publish` has completed
- **THEN** WorkflowRun SHALL fail with reason `post-publish-health-failed`
- **AND** it SHALL NOT schedule `fix-integrate-health` regardless of check failure policy configuration

### Requirement: Task completion persists clean-worktree verification evidence

WorkflowRun SHALL require clean-worktree verification evidence before marking any task as completed. A task result that lacks clean-worktree evidence SHALL be treated as incomplete, and the task SHALL NOT transition to a terminal completed state.

#### Scenario: Clean worktree is recorded in task completion evidence

- **WHEN** a task result is reported to WorkflowRun
- **AND** the result includes a successful completion status
- **THEN** the task result SHALL include clean-worktree verification evidence indicating that `git status --porcelain` returned empty output in the task workspace

#### Scenario: Task without clean-worktree evidence cannot complete

- **WHEN** a task result is reported to WorkflowRun with successful completion status
- **AND** the result does not include clean-worktree verification evidence
- **THEN** the runner MUST NOT have reported the task as completed
- **AND** the WorkflowRun SHALL treat the task as incomplete
- **AND** the task SHALL NOT be considered for stage progression

The runner-side guarantee is the enforcement point in this change. Server-side WorkflowRun validation of the clean-worktree evidence is a separate concern and is out of scope for this issue.

#### Scenario: Dirty-worktree task failure is visible in WorkflowRun

- **WHEN** a task fails with structured dirty-worktree evidence
- **THEN** the WorkflowRun task result SHALL include the structured dirty-worktree evidence
- **AND** the failure SHALL be visible in issue detail and CLI surfaces as a task failure with the dirty-worktree reason

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

WorkflowRun SHALL expose current-stage retryability using the same semantics as `retryStage(stage)` without mutating or persisting state during retryability evaluation. When the current stage is in a feedback loop (awaiting approval, feedback requested, apply-feedback pending or running), the stage SHALL NOT be considered in a failed state that requires retry. Retryability SHALL NOT apply to active feedback loop stages.

#### Scenario: Active feedback loop is not retryable

- **GIVEN** the current StageRun is running the `apply-feedback` task after a feedback request
- **WHEN** resume-pipeline evaluates retryability for that stage
- **THEN** the run SHALL NOT be considered a retryable failure
- **AND** no retry SHALL be started for the feedback loop

#### Scenario: Non-current stage is not retryable

- **GIVEN** the latest WorkflowRun has `status = failed`
- **AND** its `currentStage` differs from the issue's current stage
- **WHEN** resume-pipeline evaluates retryability for the issue stage
- **THEN** the run SHALL NOT be considered retryable
- **AND** the retry SHALL NOT be started

#### Scenario: Non-failed run is not retryable

- **GIVEN** the latest WorkflowRun has a status other than `failed`
- **WHEN** resume-pipeline evaluates retryability for the issue stage
- **THEN** the run SHALL NOT be considered a retryable current-stage failure

### Requirement: stage-approval-rejection-feedback

When a user requests changes at a stage approval gate, the system SHALL create an `ApprovalFeedback` record with the user's feedback body scoped to the workflow run and stage. The stage SHALL resume as running work rather than being marked as failed. The feedback record SHALL be visible in workflow approval history. Prior approval request context MAY be retained for audit, but it SHALL NOT replace or hide the user's feedback.

#### Scenario: Feedback request creates ApprovalFeedback and resumes stage

- **GIVEN** an issue is awaiting current-stage approval
- **WHEN** the user requests changes with feedback text
- **THEN** the system SHALL create an `ApprovalFeedback` record with the user's body, scoped to the WorkflowRun and current stage
- **AND** the StageRun SHALL resume as running
- **AND** the WorkflowRun SHALL NOT record failure evidence for `approval-rejected`
- **AND** the `apply-feedback` task SHALL be scheduled as the next work item

#### Scenario: Prior approval context does not shadow feedback

- **GIVEN** an awaiting approval already has approval request output
- **WHEN** the user requests changes with different feedback
- **THEN** the persisted `ApprovalFeedback` record SHALL expose the user's feedback
- **AND** the prior approval request output MAY only appear as separate context in approval history

#### Scenario: Feedback request is distinct from rejection failure

- **GIVEN** the user requests changes with feedback
- **WHEN** the workflow state is inspected
- **THEN** the WorkflowRun SHALL NOT have `status = failed` solely because feedback was requested
- **AND** the stage failure reason SHALL NOT be set to `approval-rejected`
- **AND** the previous approval evidence SHALL be invalidated when feedback changes the candidate

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

### Requirement: REQ-WR-RECOVERY-001 Retry failed work in current stage

WorkflowRun SHALL make retry target the failed work in the latest failed current-stage attempt. Retry SHALL preserve earlier successful same-stage work that remains valid, reset the failed work and downstream dependent work, clear current failure/approval state, and keep the WorkflowRun in the same stage.

#### Scenario: Retry failed task preserves earlier completed tasks
- **WHEN** the latest WorkflowRun is failed in the current stage because a task failed
- **AND** earlier tasks in the same stage are completed and still valid
- **THEN** retry reopens the run and current StageRun
- **AND** the failed task is reset to pending
- **AND** downstream same-stage tasks and checks are reset as needed
- **AND** earlier completed tasks remain completed
- **AND** the current stage is unchanged

#### Scenario: Retry failed check preserves completed tasks
- **WHEN** the latest WorkflowRun is failed in the current stage because a check failed
- **AND** all required tasks in that stage completed successfully
- **THEN** retry reopens the run and current StageRun
- **AND** completed tasks remain completed
- **AND** the failed check and downstream checks are reset
- **AND** work derived from that failed check is invalidated where applicable
- **AND** the current stage is unchanged

### Requirement: REQ-WR-RECOVERY-002 Rerun current stage from first work

WorkflowRun SHALL make rerun discard the current stage attempt state and restart the same stage from its first work item. Rerun SHALL clear current-stage task/check progress, failure, approval, and retry-derived state while preserving earlier passed stages and leaving currentStage unchanged.

#### Scenario: Rerun resets current stage from beginning
- **WHEN** rerun is requested for a non-backlog, non-done current stage
- **THEN** the WorkflowRun remains in the same current stage
- **AND** all current-stage tasks and checks are reset for execution from the first work item
- **AND** current-stage failure and approval state are cleared
- **AND** earlier passed stages remain passed

#### Scenario: Plan rerun makes first Plan work next
- **WHEN** rerun is requested while the current stage is Plan
- **AND** Plan artifacts from the prior attempt already exist
- **THEN** WorkflowRun reports the first Plan work as pending next work
- **AND** existing artifact files alone do not mark that work complete

### Requirement: WorkflowRun selects work across configured sources

WorkflowRun SHALL remain the authority for selecting the next task, check, approval wait, failure, or completion outcome after stage work has been materialized from configured work sources. Default tasks, dynamic Build tasks, runtime-added tasks, and repair tasks SHALL be represented as ordered StageRun task entities before they are selected for execution.

#### Scenario: Multiple work sources materialize into one StageRun task list

- **WHEN** a stage has default tasks, dynamic tasks, runtime-added tasks, or repair tasks available
- **THEN** WorkflowRun or StageRun SHALL represent them in one ordered task list for that stage
- **AND** `nextWork()` SHALL select executable tasks from that list before selecting checks or approval

#### Scenario: Runtime-added task blocks later checks

- **WHEN** a runtime-added task is pending or running in the current StageRun
- **THEN** WorkflowRun SHALL select that task according to task ordering and dependency rules
- **AND** it SHALL NOT select later checks or approval until the task reaches a successful terminal state

### Requirement: StageRun records source and policy-driven work consistently

StageRun SHALL record task and check state consistently regardless of whether the work came from default stage definitions, Ralph dynamic loading, runtime-added actions, or repair policy scheduling.

#### Scenario: Static and dynamic tasks share task semantics

- **WHEN** a static Plan task, static Integrate task, Ralph Build task, repair task, or runtime-added rebase task is materialized
- **THEN** the task SHALL have a stable id, title, status, order, source or causedBy metadata when applicable, attempts, output, and failure evidence
- **AND** task failure SHALL block later task, check, approval, and stage completion decisions through the same WorkflowRun semantics

#### Scenario: Checks share check semantics

- **WHEN** a check is declared by a stage definition or materialized for persistence
- **THEN** the check SHALL have a stable name, title, status, output, and run evidence
- **AND** check results SHALL be interpreted by WorkflowRun policy rather than by check implementation side effects

### Requirement: Approval is separate from checks in WorkflowRun decisions

WorkflowRun SHALL model approval as a user decision point owned by StageRun state, not as ordinary repairable check work. Runtime-added tasks and invalidation policy MAY cause a stage to leave an approval wait state, but approval SHALL only be invalidated when policy facts require it.

#### Scenario: Approval wait follows successful checks

- **WHEN** a stage requires approval and all required tasks and checks have passed
- **THEN** WorkflowRun SHALL place the StageRun in awaiting approval state
- **AND** it SHALL expose approval as the next workflow decision rather than scheduling a repair task

#### Scenario: Runtime task does not blindly erase approval evidence

- **WHEN** a runtime-added task is appended while a stage is awaiting approval
- **THEN** the StageRun SHALL become runnable so the task can execute
- **AND** prior approval evidence SHALL only be invalidated according to the configured invalidation policy and task result facts

### Requirement: Rebase task reports facts before invalidation decisions

WorkflowRun SHALL treat `rebase-branch` as ordinary task work whose result reports branch facts. Dependent review, check, and approval invalidation SHALL be driven by stage invalidation policy and reported facts rather than by the mere presence of a rebase request.

#### Scenario: Rebase changed snapshot invalidates dependent state

- **WHEN** `rebase-branch` completes successfully and reports that the candidate snapshot changed
- **THEN** WorkflowRun SHALL invalidate the dependent tasks, checks, and approval state declared by the current stage invalidation policy
- **AND** later work SHALL re-run against the new snapshot before approval can pass

#### Scenario: Rebase unchanged snapshot preserves dependent state

- **WHEN** `rebase-branch` completes successfully and reports that the candidate snapshot did not change
- **THEN** WorkflowRun SHALL preserve dependent task, check, and approval state unless another configured invalidation policy applies

#### Scenario: Rebase failure blocks workflow

- **WHEN** `rebase-branch` fails
- **THEN** WorkflowRun SHALL fail the current StageRun through ordinary task failure semantics
- **AND** later tasks, checks, and approval SHALL NOT execute

### Requirement: WorkflowRun stage completion requires promised run evidence

WorkflowRun SHALL decide stage completion by comparing the active StageDefinition promise with the active StageRun evidence, and SHALL NOT treat an empty remaining work queue or vacuous task/check collection as successful completion.

#### Scenario: Missing static task evidence blocks completion
- **GIVEN** a stage definition declares a static task
- **WHEN** the matching StageRun has no corresponding successful terminal TaskRun
- **THEN** WorkflowRun SHALL report the stage as not complete with a recoverable missing-task-evidence reason
- **AND** `nextWork()` and explicit completion paths SHALL NOT advance the stage

#### Scenario: Missing static check evidence blocks completion
- **GIVEN** a stage definition declares a required check
- **WHEN** the matching StageRun has no corresponding current passed CheckRun
- **THEN** WorkflowRun SHALL report the stage as not complete with a recoverable missing-check-evidence reason
- **AND** `nextWork()` and explicit completion paths SHALL NOT advance the stage

#### Scenario: Existing run-owned work must finish successfully
- **GIVEN** a StageRun contains a dynamic, repair, rebase, retry, or convergence TaskRun
- **WHEN** that TaskRun is pending, running, failed, or skipped
- **THEN** WorkflowRun SHALL prevent later checks, approval, stage completion, and workflow completion
- **AND** the task SHALL remain required evidence for this run until it reaches a successful terminal state

#### Scenario: Required approval is explicit evidence
- **GIVEN** a stage requires user approval
- **WHEN** all required tasks and checks have passed but approval has not been approved
- **THEN** WorkflowRun SHALL return an approval wait instead of stage completion

### Requirement: Build dynamic work source evaluation is completion evidence

WorkflowRun SHALL require Build dynamic work source evaluation evidence before Build can complete. Generated Build task identities SHALL be materialized into the Build StageRun as run-owned TaskRun records and SHALL NOT be copied into static StageDefinition tasks.

#### Scenario: Unevaluated Build work source blocks completion
- **GIVEN** the Build stage uses a dynamic work source such as `tasks.json`
- **WHEN** the Build StageRun has no recorded source evaluation state
- **THEN** WorkflowRun SHALL block Build completion with a dynamic-source-not-evaluated reason

#### Scenario: Missing invalid or empty Build source blocks completion
- **WHEN** Build source evaluation records that `tasks.json` is missing, invalid, or contains zero tasks
- **THEN** WorkflowRun SHALL block Build completion with a clear recoverable source failure reason
- **AND** the issue SHALL NOT advance as if Build had no required work

#### Scenario: Materialized Build tasks become required run evidence
- **WHEN** Build source evaluation produces one or more tasks
- **THEN** the system SHALL append or preserve those tasks as Build StageRun TaskRun records
- **AND** every materialized Build TaskRun SHALL participate in the shared completion guard for that run

### Requirement: Check completion uses current review and merge evidence

Check completion SHALL depend on current StageRun task/check evidence for the candidate being approved, not raw AgentSession status or absence of work.

#### Scenario: Missing current review evidence blocks Check
- **WHEN** Check lacks a successful current AI review task or equivalent authoritative review result for the current candidate
- **THEN** WorkflowRun SHALL prevent Check completion and approval

#### Scenario: Missing review or merge checks block Check
- **WHEN** Check lacks current passed required review-verdict, verification, or merge-readiness CheckRun evidence
- **THEN** WorkflowRun SHALL prevent Check completion and approval

#### Scenario: Stale session status is not authoritative
- **GIVEN** an earlier AgentSession failed
- **WHEN** later current StageRun task/check evidence proves Check and Integrate success
- **THEN** WorkflowRun completion SHALL NOT be blocked solely by the stale failed session
- **AND** a later successful AgentSession SHALL NOT substitute for missing StageRun evidence

### Requirement: Integrate evidence is required for final workflow completion

WorkflowRun SHALL require the workflow to pass the final Integrate StageRun with required task, check, and delivery evidence before reporting workflow completion.

#### Scenario: Workflow cannot pass before final stage
- **WHEN** a WorkflowRun has not reached and passed the configured final Integrate stage
- **THEN** WorkflowRun SHALL NOT report the run as passed

#### Scenario: Missing Integrate delivery evidence blocks Done
- **WHEN** Integrate lacks successful required TaskRun evidence, passed required CheckRun evidence, or delivery facts such as spec sync, archive, prepare, publish, or final health evidence required by the Integrate model
- **THEN** WorkflowRun SHALL block final completion with a clear reason
- **AND** delivery action state alone SHALL NOT mark the workflow passed

### Requirement: TaskRun references shared agent sessions without owning transcripts

WorkflowRun task execution SHALL preserve TaskRun as the user-visible work unit and SHALL allow an agent-backed TaskRun to reference at most one logical agent session reference. Multiple TaskRuns MAY reference the same logical session, but task completion, failure, attempts, duration, output, and artifact evidence SHALL remain task-owned state.

#### Scenario: Shared session preserves separate task results
- **WHEN** Plan executes `proposal`, `specs`, `design`, `tasks`, and `self-review` through the same `agentSessionRef`
- **THEN** the Plan StageRun SHALL record independent TaskRun results for each task
- **AND** WorkflowRun SHALL NOT infer task completion from the referenced session reaching a terminal state

#### Scenario: Session failure is task evidence
- **WHEN** execution against a named agent session fails for one task
- **THEN** the task attempt SHALL record failed task evidence
- **AND** WorkflowRun SHALL decide retry, failure, or approval behavior through normal task and stage policy rather than session status alone

### Requirement: Stage attempt boundaries create fresh named sessions

Named agent session references SHALL resolve within the active WorkflowRun StageRun attempt. Retry, rerun, or rewind of a stage SHALL create a fresh real session for the same logical ref instead of appending prompts to an old completed transcript.

#### Scenario: Same attempt reuses named session
- **WHEN** multiple non-restored tasks in the same stage attempt use `agentSessionRef: "plan-artifacts"`
- **THEN** they SHALL resolve to the same real agent session instance

#### Scenario: New attempt does not append to old transcript
- **WHEN** the Plan stage is retried, rerun, or rewound after a previous attempt used `agentSessionRef: "plan-artifacts"`
- **THEN** the new stage attempt SHALL resolve `plan-artifacts` to a new real agent session instance
- **AND** the old session SHALL remain historical evidence

#### Scenario: Restore and skip do not change later ownership
- **WHEN** an intermediate Plan artifact task is restored from disk or skipped
- **THEN** that restored or skipped task SHALL NOT create a session solely because its policy has `agentSessionRef`
- **AND** later non-restored tasks SHALL still resolve their configured ref deterministically

### Requirement: Work item attempts belong to stage work items

WorkflowRun SHALL model execution attempts on stage work items, not on the workflow run itself. A work item MAY be a task or a check within a StageRun. The latest work item attempt SHALL carry state `running`, `completed`, `failed`, or `interrupted`, attempt number, timestamps, diagnostic output or error details, and runtime evidence identifiers when available.

#### Scenario: Task attempt is persisted and reloaded

- **WHEN** a stage task starts execution
- **THEN** WorkflowRun SHALL record a latest task attempt with state `running`
- **AND** repository reload SHALL preserve that latest attempt and any previous attempt history or equivalent snapshot data

#### Scenario: Check attempt is persisted and reloaded

- **WHEN** a stage check starts execution
- **THEN** WorkflowRun SHALL record a latest check attempt with state `running`
- **AND** repository reload SHALL preserve that latest attempt and any previous attempt history or equivalent snapshot data

#### Scenario: Existing work state synthesizes latest attempts

- **WHEN** existing task or check rows are loaded without explicit attempt history
- **THEN** completed or passed work SHALL project to a completed latest attempt
- **AND** failed or error work SHALL project to a failed latest attempt
- **AND** running work SHALL project to a running latest attempt until reconciliation proves interruption
- **AND** pending work SHALL have no latest attempt until execution starts

### Requirement: Attempt transitions keep work progress consistent

WorkflowRun SHALL update work item progress and latest attempt state through one aggregate transition so task or check status cannot drift from the latest attempt within one save operation.

#### Scenario: Completed attempt completes work

- **WHEN** a task or check attempt completes successfully
- **THEN** the latest attempt state SHALL become `completed`
- **AND** the corresponding task or check progress SHALL become completed or passed according to work item type

#### Scenario: Failed attempt fails work

- **WHEN** a task or check handler produces a genuine failed result
- **THEN** the latest attempt state SHALL become `failed`
- **AND** the corresponding task or check progress SHALL become failed or error according to existing stage policy

#### Scenario: Interrupted attempt leaves work incomplete

- **WHEN** a running task or check attempt is interrupted by stopped or lost execution
- **THEN** the latest attempt state SHALL become `interrupted`
- **AND** the work item SHALL remain incomplete
- **AND** the attempt SHALL NOT be treated as a failed result

### Requirement: Workflow recovery summary is derived from work progress

WorkflowRun SHALL expose a workflow recovery summary derived from current stage work progress and latest attempt state. The summary SHALL include at least `running`, `awaiting-approval`, `waiting-for-recovery`, and `completed`.

#### Scenario: Interrupted latest attempt is not active running

- **WHEN** the current work item's latest attempt is `interrupted`
- **THEN** the workflow recovery summary SHALL be `waiting-for-recovery`
- **AND** user-facing workflow state SHALL NOT claim that the current work is actively running

#### Scenario: Non-running latest attempt cannot project active running

- **WHEN** the current work item's latest attempt is `completed`, `failed`, `interrupted`, or absent
- **THEN** the workflow recovery summary SHALL NOT be `running` unless another current work item has a valid live running attempt

#### Scenario: Rerun creates fresh stage attempts

- **WHEN** the current stage is rerun
- **THEN** the stage's work items SHALL receive fresh execution attempts as they execute
- **AND** rerun SHALL NOT reinterpret interrupted attempts as failed retry attempts

### Requirement: REQ-WR-001 workflow run records structured convergence evidence

Workflow runs SHALL preserve structured task, check, and reaction outputs as runtime evidence for convergence decisions.

#### Scenario: Failed check context is assembled from structured outputs

- **WHEN** a check fails with structured items
- **THEN** the workflow run SHALL build failed-check context containing check identity, parsed verdict, blocking items, non-blocking items, source artifact references, snapshot metadata, and relevant prior task outputs
- **AND** reaction tasks SHALL receive this bounded context instead of scraping unstructured prose

#### Scenario: Reaction outputs drive verification rechecks

- **WHEN** a reaction task completes
- **THEN** the workflow run SHALL record attempted, resolved, unresolved, and newly observed item IDs
- **AND** the configured task/check path SHALL re-run in verification mode before the failed check can become passed

### Requirement: apply-feedback task is a normal WorkflowRun task

The `apply-feedback` task SHALL be scheduled as an ordinary WorkflowRun task in the current StageRun with `causedBy` metadata referencing the feedback id. It SHALL participate in normal task ordering, status transitions, and completion guards.

#### Scenario: Feedback task appears in StageRun task list

- **WHEN** a user requests changes at an approval gate
- **THEN** the current StageRun SHALL append an `apply-feedback` task
- **AND** the task SHALL carry `causedBy` metadata with the feedback id
- **AND** the task SHALL appear before later checks and approval in the task ordering

#### Scenario: Feedback task failure blocks stage completion

- **WHEN** the `apply-feedback` task fails
- **THEN** the current StageRun SHALL fail through normal task failure semantics
- **AND** later checks and approval SHALL NOT execute until the failure is addressed

#### Scenario: Feedback task completion invalidates stale checks

- **WHEN** the `apply-feedback` task completes successfully
- **AND** the task changed code or stage artifacts
- **THEN** dependent checks and prior approval evidence SHALL be invalidated
- **AND** checks SHALL rerun before approval can be requested again

### Requirement: Workflow run records feedback as structured evidence

Workflow runs SHALL preserve structured feedback evidence including the feedback id, body, status, resolution task id, and resolution summary as runtime evidence for the feedback loop cycle.

#### Scenario: Feedback evidence is queryable from WorkflowRun

- **WHEN** an `ApprovalFeedback` record exists for a WorkflowRun
- **THEN** the feedback id, stage, status, and body SHALL be accessible from WorkflowRun evidence
- **AND** the feedback SHALL be included in approval history projections

#### Scenario: Resolved feedback records resolution evidence

- **WHEN** an `apply-feedback` task completes with a resolution summary
- **THEN** the WorkflowRun SHALL record that the corresponding feedback has been resolved
- **AND** the resolution summary and resolution task id SHALL be preserved

### Requirement: Task lifecycle transitions through Running on dispatch

A TaskRun SHALL transition from `Pending` to `Running` when the workflow grain dispatches the task to a runner. The transition SHALL set `StartedAt` to the dispatch time, record the `RunnerId` of the runner that claimed the run, and record the `WorkId` assigned to the dispatch. A `TaskStarted` domain event SHALL be emitted on the `Pending` -> `Running` transition. A task SHALL NOT reach `Completed` or `Failed` from `Pending` without first passing through `Running`.

#### Scenario: Dispatched task enters Running

- **WHEN** the workflow grain dispatches a `Pending` task to a runner
- **THEN** the TaskRun SHALL transition to `Running`
- **AND** `StartedAt` SHALL be set to the dispatch timestamp
- **AND** `RunnerId` SHALL be set to the claiming runner
- **AND** `WorkId` SHALL be set to the assigned dispatch work identifier
- **AND** a `TaskStarted(Stage, TaskId, RunnerId)` domain event SHALL be emitted

#### Scenario: Successful result completes a Running task

- **WHEN** a `Running` task receives a successful result
- **THEN** the TaskRun SHALL transition to `Completed`
- **AND** the existing `TaskCompleted` event SHALL be emitted

#### Scenario: Failed result fails a Running task

- **WHEN** a `Running` task receives a failed result
- **THEN** the TaskRun SHALL transition to `Failed`
- **AND** the existing `TaskFailed` event SHALL be emitted

### Requirement: Task lifecycle records completion timestamps

A TaskRun SHALL record `FinishedAt` when it transitions to `Completed` or `Failed`. The timestamp SHALL reflect when the result was processed by the workflow grain, not when the runner locally finished execution. `StartedAt` and `FinishedAt` together SHALL provide an observable dispatch-to-completion duration for every task.

#### Scenario: Completion sets FinishedAt

- **WHEN** a `Running` task transitions to `Completed` or `Failed`
- **THEN** `FinishedAt` SHALL be set to the result-processing time
- **AND** both `StartedAt` and `FinishedAt` SHALL be populated on the terminal TaskRun

### Requirement: TaskRun is the single source of truth for in-flight dispatch

The workflow grain SHALL use TaskRun state as the single source of truth for in-flight task dispatch. Dispatch recovery on grain reactivation, idempotent dispatch decisions, and result matching SHALL read from TaskRun fields (`Status == Running`, `RunnerId`, `WorkId`) rather than a separate lease persistent state. A separate `WorkLease` persistent state SHALL NOT exist on the workflow grain for task dispatch tracking.

#### Scenario: Reactivation restores dispatch from the Running task

- **WHEN** the workflow grain reactivates and a TaskRun is in `Running` state
- **THEN** `RunCoreAsync` SHALL restore the dispatch from the TaskRun's recorded `WorkId` and `RunnerId`
- **AND** the restored dispatch SHALL be re-assigned to the claiming runner

#### Scenario: In-flight check uses Running task instead of lease

- **WHEN** `RunCoreAsync` evaluates whether work is already in-flight
- **THEN** it SHALL detect in-flight work by scanning for a `Running` TaskRun
- **AND** it SHALL NOT read from a separate lease persistent state

#### Scenario: Result matching uses the Running task WorkId

- **WHEN** `ReportResultAsync` receives a result for a `workId`
- **THEN** it SHALL match the incoming `workId` against the `WorkId` of the `Running` TaskRun
- **AND** it SHALL match the reporting `runnerId` against the TaskRun's `RunnerId`
- **AND** a result that does not match the Running task's `WorkId` and `RunnerId` SHALL be ignored

### Requirement: StageCheck carries dispatch metadata for lease-free recovery

A StageCheck SHALL carry `DispatchWorkId`, `DispatchRunnerId`, and `DispatchedAt` fields when dispatched to a runner. The in-flight signal for a dispatched check SHALL be `DispatchWorkId != null && Status == Pending`. StageCheck SHALL NOT gain a `Running` status value or new domain events; its lifecycle SHALL remain `Pending -> Passed | Failed`.

#### Scenario: Dispatched check records dispatch metadata

- **WHEN** the workflow grain dispatches a `Pending` check to a runner
- **THEN** `DispatchWorkId`, `DispatchRunnerId`, and `DispatchedAt` SHALL be set on the StageCheck
- **AND** the check `Status` SHALL remain `Pending`

#### Scenario: Check result clears dispatch metadata

- **WHEN** a dispatched check receives a result
- **THEN** the check SHALL transition to `Passed` or `Failed`
- **AND** the dispatch metadata SHALL be cleared

#### Scenario: Reactivation recovers a dispatched check

- **WHEN** the workflow grain reactivates and a check has `DispatchWorkId` set with `Status == Pending`
- **THEN** the workflow SHALL re-dispatch or recover the check based on runner liveness
- **AND** the check SHALL NOT be silently treated as completed
