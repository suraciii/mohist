# OpenSpec Capability: pipeline-model

### Requirement: REQ-PM-002 No fallback chain for first fix policy

The first check failure policy implementation SHALL NOT introduce fallback-to-plan, fallback-to-build, fallback ask-user, nested reaction chains, or multi-stage failure policies. When fix attempts are exhausted, the stage SHALL remain failed or paused with visible evidence.

#### Scenario: Exhausted fix attempts do not change stage
- **WHEN** a check fails after all configured fix attempts
- **THEN** the issue SHALL remain in the current stage state for user or later workflow recovery
- **AND** the failed check result and fix task result SHALL remain visible

### Requirement: REQ-PM-003 CHECK defers recoverable OpenSpec sync conflicts

CHECK SHALL NOT hard-block issue progression solely because OpenSpec sync preview detects a recoverable delta classification conflict such as `missing_source` for a requirement written under `MODIFIED Requirements`. CHECK MAY record read-only preview evidence, but durable updates to `openspec/specs/` SHALL remain an INTEGRATE responsibility.

#### Scenario: Missing source preview does not block CHECK
- **WHEN** CHECK runs OpenSpec sync preview for a change delta
- **AND** the preview reports `missing_source` for a `MODIFIED` requirement that may be resolved during integration
- **THEN** CHECK SHALL NOT fail solely because of that preview conflict
- **AND** CHECK SHALL NOT write to `openspec/specs/`
- **AND** the preview evidence, if collected, SHALL remain visible as advisory output

#### Scenario: Non-OpenSpec CHECK gates still block
- **WHEN** CHECK runs health, merge readiness, AI review, or user approval checks
- **THEN** those checks SHALL retain their existing blocking semantics

### Requirement: REQ-PM-004 Integrate spec sync failure remains local

When `integrate:spec-sync` fails, the workflow SHALL keep the issue at INTEGRATE or an interrupted/blocked-at-INTEGRATE state with visible failure evidence. The workflow SHALL NOT automatically fall back to PLAN, BUILD, or CHECK, and SHALL NOT automatically rerun the entire pipeline.

#### Scenario: Spec sync failure stops at INTEGRATE
- **WHEN** `integrate:spec-sync` fails due to sync resolution or validation
- **THEN** the issue SHALL remain associated with INTEGRATE failure state
- **AND** `integrate:archive-change`, `integrate:merge`, and `final-health` SHALL NOT run
- **AND** the failure output SHALL identify the failing step as `integrate:spec-sync`

### Requirement: CHECK stage exposes review and merge decisions

The CHECK stage SHALL present one initial user-visible task, `ai-review`, followed by the user-visible checks `review-passed`, `merge-ready`, and `user-approval`. Internal health gates, integration preview evidence, review artifact retries, and implementation-specific validation SHALL NOT be exposed as separate user-facing CHECK-stage checks.

#### Scenario: Check stage starts with ai-review task

- **WHEN** a default CHECK stage starts
- **THEN** the initial user-visible task SHALL be `ai-review`
- **AND** `ai-review` SHALL be represented as task history, not as a check result

#### Scenario: Check stage visible checks are simplified

- **WHEN** CHECK-stage results are presented to users
- **THEN** the visible automated checks SHALL be `review-passed` and `merge-ready`
- **AND** the visible approval point SHALL be `user-approval`
- **AND** users SHALL NOT need to interpret `health:check`, `merge-readiness`, `integration-health-gate-preview`, or `ai-review` as check names

#### Scenario: Internal evidence stays internal

- **WHEN** CHECK-stage execution gathers health, integration-preview, artifact-retry, or repair evidence
- **THEN** that evidence MAY appear in task output, logs, or diagnostic details
- **AND** it SHALL NOT create additional user-visible check-stage decision points

### Requirement: Collected check evidence remains visible through repair

Pipeline stage execution SHALL preserve the complete initial check evidence for a phase even when a later repair task is attempted. Repair handling may change the current effective result, but it SHALL NOT reduce the user's visibility back to only the first discovered failure.

#### Scenario: Repairable failure still shows full initial diagnosis

- **WHEN** a phase initially reports multiple failing non-approval checks
- **AND** the earliest repairable failure triggers a fix task
- **THEN** the phase history SHALL still show the full initial collected result set
- **AND** the fix task plus recheck results SHALL be visible alongside that baseline evidence

#### Scenario: Later checks rerun after successful repair

- **WHEN** a fix task makes the targeted failing check pass on recheck
- **THEN** the workflow SHALL continue running later checks from that point using the repaired state
- **AND** it SHALL preserve the existing semantic that downstream checks are not skipped forever after an earlier repair succeeds

### Requirement: Exhausted or unrepairable failures remain local with full evidence

When collected phase failures cannot be repaired or remain failing after allowed attempts, the workflow SHALL stay in the current stage with complete evidence visible. It SHALL NOT fall back to another stage or collapse the visible diagnosis back to the first failure only.

#### Scenario: Failure without policy remains local

- **WHEN** a collected non-approval check result is `fail` or `error`
- **AND** no `CheckFailurePolicy` exists for that check
- **THEN** the workflow SHALL keep the issue in the current stage state
- **AND** the collected phase evidence SHALL remain visible to the user

#### Scenario: Exhausted repair attempts preserve evidence

- **WHEN** a collected failed or errored non-approval check has a fix policy
- **AND** the check still does not pass after the configured max attempts
- **THEN** the workflow SHALL keep the failed check results and fix task results visible
- **AND** it SHALL NOT automatically fall back to plan, build, or another escalation path

### Requirement: canonical pipeline stage model (REQ-001)

The system SHALL use a single canonical pipeline stage model shared by backend and frontend: `backlog`, `plan`, `build`, `check`, `integrate`, `done`.

#### Scenario: Deprecated stage values are not legal pipeline stages
- **WHEN** stage values are validated, compared, or serialized for issue pipeline state
- **THEN** `draft` and `explore` are not accepted as legal pipeline stage values
- **AND** `backlog`, `plan`, `build`, `check`, `integrate`, and `done` remain supported

### Requirement: canonical stage order and transitions (REQ-002)

The system SHALL order and transition pipeline stages using the real user-visible flow.

#### Scenario: Stage order matches the real pipeline
- **WHEN** the system compares stage order or computes forward progression
- **THEN** it uses `backlog -> plan -> build -> check -> integrate -> done`

#### Scenario: Pipeline start enters plan from backlog
- **WHEN** an issue is created and then started
- **THEN** it begins in `backlog`
- **AND** starting the pipeline advances it to `plan`

#### Scenario: Check approval advances into integrate
- **WHEN** Check is approved
- **THEN** the issue advances to `integrate`
- **AND** it does not skip directly to `done`

#### Scenario: Recovery loops do not depend on deprecated stages
- **WHEN** the system validates a recovery or retry path
- **THEN** any allowed non-linear transition uses real pipeline stages such as `check -> build` or `integrate -> build`
- **AND** no legality check depends on `draft` or `explore`

### Requirement: REQ-PM-001 Stage task check boundaries are explicit

Pipeline stages SHALL present one canonical user-visible task list and one separate user-visible check list per stage. Every visible task SHALL represent a real workflow execution unit, and repairs triggered by failed checks SHALL remain tasks in that same list rather than becoming a second task category or a check surrogate.

#### Scenario: Placeholder rows are not visible tasks

- **WHEN** a stage contains stored placeholder rows that do not correspond to real executable workflow work
- **THEN** those rows SHALL NOT appear in the user-visible stage task list
- **AND** the stage SHALL instead show only real workflow tasks that executed, are executing, or were actually added for retry or repair

#### Scenario: Runtime repair stays in the same task list

- **WHEN** a check failure causes a repair task such as `repair-plan-artifacts`, `fix-build-health`, `fix-review-findings`, `repair-merge`, or rebase-related work to be added
- **THEN** that repair SHALL appear in the same stage task list as the original stage work
- **AND** the task MAY include explanation metadata describing why it was added

#### Scenario: Checks remain distinct from tasks

- **WHEN** a stage reports task progress, check results, and approval state
- **THEN** tasks SHALL be shown in the stage task list
- **AND** checks SHALL be shown in a separate check list
- **AND** approval SHALL remain decision state rather than becoming a synthetic task

### Requirement: REQ-PM-WORKFLOW-RUN-001 Pipeline current state is rooted in WorkflowRun

Pipeline current state SHALL be represented and decided by a WorkflowRun aggregate containing ordered StageRuns, Tasks, Checks, approval snapshots, failure reasons, and delivery metadata. Issue stage/status remain coarse projections, `stage_executions` and logs remain evidence, and checkpoints remain resume cursors.

#### Scenario: Current state has one runtime root

- **WHEN** a user, API consumer, runner, or recovery path asks where an issue run currently is
- **THEN** the system SHALL answer from WorkflowRun status, currentStage, StageRuns, tasks, checks, approval snapshots, and failure reason
- **AND** it SHALL NOT require consumers to combine issue stage, `tasks.json`, `stage_states`, execution logs, session logs, check suites, and checkpoints to understand current progress

#### Scenario: Stage transition cannot bypass aggregate

- **WHEN** any workflow path wants to start a stage, complete a stage, fail a stage, or advance to the next stage
- **THEN** it SHALL invoke a WorkflowRun or StageRun domain method
- **AND** it SHALL NOT update issue stage/status or WorkflowRun stage status as the business decision source

#### Scenario: Stage organizes tasks and checks

- **WHEN** a stage contains task progress, check results, and approval state
- **THEN** tasks SHALL remain executable work units
- **AND** checks SHALL remain read-only validators
- **AND** approval SHALL remain user decision state rather than executable check side effect

### Requirement: REQ-PM-007 Integrate failures stay local with visible task/check evidence

Integrate SHALL have the same visible runtime lifecycle as other runnable stages, with task failures stopping the stage locally and post-task check failures remaining visible in Integrate. A post-merge health failure SHALL be distinguished from ordinary repairable check failures because merge delivery has already occurred.

#### Scenario: Task failure stops later Integrate work

- **WHEN** `integrate:spec-sync`, `integrate:archive-change`, or `integrate:merge` fails
- **THEN** later Integrate tasks and checks SHALL NOT run
- **AND** the issue SHALL remain in Integrate failure state with the failing task visible

#### Scenario: Final health failure after merge requires manual intervention

- **WHEN** `health:integrate` fails after merge succeeds
- **THEN** the issue SHALL show that merge already happened
- **AND** WorkflowRun SHALL fail with post-merge delivery failure evidence rather than scheduling an automatic fix task

### Requirement: blocked-retryable-current-stage-resume

The pipeline queue SHALL treat a blocked issue as runnable by `resume-pipeline` only when the latest WorkflowRun represents a retryable failed run at the issue's current stage. Blocked issues without a retryable current-stage failed WorkflowRun SHALL remain skipped or non-runnable.

#### Scenario: Retryable blocked current-stage failure runs
- **GIVEN** an issue has `status=blocked`
- **AND** the issue's current stage is `plan`
- **AND** the latest WorkflowRun has `status=failed` and `currentStage=plan`
- **AND** WorkflowRun retryability accepts retrying `plan`
- **WHEN** a `resume-pipeline` queue task is evaluated
- **THEN** the queue task SHALL NOT complete as `skipped` solely because the issue is blocked
- **AND** normal pipeline execution SHALL begin so the WorkflowRun retry for `plan` can start

#### Scenario: Genuinely blocked issue remains skipped
- **GIVEN** an issue has `status=blocked`
- **AND** there is no latest WorkflowRun that is retryable for the issue's current stage
- **WHEN** a `resume-pipeline` queue task is evaluated
- **THEN** the queue task SHALL remain skipped or non-runnable according to existing blocked-issue behavior
- **AND** the issue SHALL NOT be broadly unblocked

#### Scenario: Approved approval continuation still resumes
- **GIVEN** an issue has `status=blocked`
- **AND** the issue has current-stage approved approval state
- **WHEN** a `resume-pipeline` queue task is evaluated
- **THEN** the existing approved-continuation path SHALL still clear blocked state and resume the pipeline

### Requirement: rejected-approval-resume-regression

Regression coverage SHALL prove that approval rejection enqueues runnable same-stage retry work and that non-retryable blocked issues remain non-runnable.

#### Scenario: Rejected Plan approval starts same-stage retry
- **GIVEN** an issue is awaiting approval at `Stage.name = "plan"`
- **WHEN** the approval is rejected with feedback
- **AND** the queued `resume-pipeline` task runs
- **THEN** the task SHALL start a retry of the `plan` stage instead of completing as `skipped`
- **AND** the retried stage SHALL request approval again after regenerated artifacts are ready

#### Scenario: Non-retryable blocked resume remains skipped
- **GIVEN** an issue is blocked without a retryable latest current-stage failed WorkflowRun
- **WHEN** a `resume-pipeline` task runs
- **THEN** the task SHALL complete as skipped or leave the issue non-runnable
- **AND** no new same-stage attempt SHALL be started

### Requirement: Done projection follows completed WorkflowRun evidence

Pipeline projection SHALL display Done only when WorkflowRun evidence proves the workflow passed through the final Integrate stage and SHALL defensively reject impossible passed snapshots.

#### Scenario: Passed snapshot before Integrate is rejected
- **WHEN** a WorkflowRun snapshot is marked passed but did not reach and pass the final Integrate stage
- **THEN** projection SHALL refuse to mark the issue Done
- **AND** it SHALL surface a diagnostic or blocked projection result

#### Scenario: Missing final evidence is rejected
- **WHEN** a WorkflowRun snapshot is marked passed but final-stage task, check, or delivery evidence is missing
- **THEN** projection SHALL refuse to mark the issue Done
- **AND** it SHALL not invent completion truth from issue stage, merge state, or session status

#### Scenario: Stale failed session does not override later workflow success
- **GIVEN** an older AgentSession failed
- **WHEN** the latest WorkflowRun evidence proves all required stages including Integrate completed successfully
- **THEN** projection SHALL allow Done despite the stale failed session

#### Scenario: Merge state alone is insufficient
- **WHEN** repository merge state indicates a merge or merged branch but WorkflowRun completion evidence is incomplete
- **THEN** projection SHALL not mark the issue Done from merge state alone

### Requirement: REQ-PM-STRUCTURED-001 pipeline stage state exposes generic convergence status

Pipeline stage state SHALL expose generic convergence status derived from authoritative structured task, check, and reaction outputs.

#### Scenario: Stage state includes convergence fields

- **WHEN** a stage is blocked by a structured failed check or is recovering through reactions
- **THEN** stage state SHALL include failed check, blocking item count, directly repaired count, reaction attempts, attempted item IDs, resolved item IDs, unresolved item IDs, new blocking item IDs, non-blocking item IDs, and blocked reason
- **AND** these fields SHALL be computed from stored structured outputs rather than parsing messages or artifacts in presentation code

#### Scenario: No convergence state is available

- **WHEN** a stage has no structured failure or older records do not contain structured result data
- **THEN** existing stage-state fields SHALL remain available
- **AND** consumers SHALL NOT be required to infer convergence from prose
