# OpenSpec Capability: workflow-engine

### Requirement: REQ-WFE-003 AI review repair is an explicit task

AI review repair SHALL be represented as an explicit check-stage task rather than a check method. The AI review check SHALL parse durable review artifacts and return verdict evidence, while `fix-review-findings` performs any code-changing repair work.

#### Scenario: Failed AI review schedules fix task
- **WHEN** `ai-review` returns a failing verdict and has a `fix-review-findings` policy
- **THEN** the check stage SHALL run a `fix-review-findings` task
- **AND** the task result SHALL be visible in task history
- **AND** `ai-review` SHALL be re-run after the task completes

### Requirement: REQ-WFE-004 Build tasks may produce no durable artifacts

The workflow engine SHALL allow task results to have an empty durable artifact list. A completed task with `artifacts: []` SHALL be valid when the task changes code, runs a command, records transient execution output, or performs a fix without producing a durable workflow file.

#### Scenario: Build task completes without artifacts
- **WHEN** the build stage completes implementation work that changes code but produces no workflow artifact
- **THEN** its task result SHALL be valid with `artifacts: []`
- **AND** transient execution details MAY be stored in task `output` or execution logs

### Requirement: Workflow uses issue-aware model resolution

The effective coder agent configuration SHALL be fixed once, at issue creation, by generically merging the issue workflow profile's `Variables` from project-level and global-level `VariableBundle`s (project values win, global values fill gaps, symmetric for `vars` and each `stages.<stage>.vars`). Runtime workflow execution and issue-bound recovery sessions SHALL read that pre-merged `Variables` directly and SHALL NOT run a per-stage model fallback chain at execution time. `BuildVariables` SHALL return the pre-merged bundle (plus context variables) without recomputing agent config. Per-stage agent dispatch SHALL read the ordinary variable key `Variables.stages[stage].vars.agent`, falling back to `Variables.vars.agent` — both ordinary variable lookups with no agent-specific resolution code.

#### Scenario: Effective agent is resolved at issue creation, not at runtime

- **WHEN** an issue is created
- **THEN** the issue workflow profile's `Variables` SHALL be populated by a generic merge of project and global `VariableBundle`s
- **AND** runtime `BuildVariables` SHALL return that pre-merged bundle (plus context variables) without recomputing agent config

#### Scenario: Stage override wins over default agent

- **WHEN** the merged `Variables.stages.build.vars.agent` defines a model
- **AND** `Variables.vars.agent` also defines a model
- **THEN** the build-stage coder session SHALL use the stage-scoped agent value
- **AND** the dispatch SHALL read it as the ordinary variable lookup `Variables.stages[stage].vars.agent`

#### Scenario: Default agent applies when no stage override exists

- **WHEN** no agent variable exists for the current stage in `Variables.stages`
- **AND** `Variables.vars.agent` defines a model
- **THEN** the coder session SHALL use `Variables.vars.agent`
- **AND** the fallback SHALL be an ordinary variable lookup with no cross-layer resolution

#### Scenario: Global configuration remains fallback through the T1 merge

- **WHEN** an agent variable is absent from the project `Variables`
- **AND** the global `Variables` provides it
- **THEN** the merged issue `Variables` SHALL contain the global value
- **AND** later changes to global config SHALL apply to newly created issues without repackaging the runtime resolution path

#### Scenario: Recovery sessions read the same pre-merged Variables

- **WHEN** conflict resolution or build-error-fix starts an issue-bound coder session
- **THEN** the session SHALL resolve its agent model from the issue's pre-merged `Variables`
- **AND** it SHALL NOT run an independent runtime fallback chain

### Requirement: REQ-WFE-005 Intelligent spec sync resolves obvious delta classification mistakes

The workflow engine SHALL provide an intelligent OpenSpec sync path for `integrate:spec-sync` that can absorb obvious requirement-level delta classification mistakes while preserving strict validation. At minimum, when a `MODIFIED` requirement has no matching source requirement in the main spec, has no rename ambiguity, and does not duplicate an existing target requirement, the sync path SHALL apply it as an added requirement and record the correction. After the intelligent sync writes spec changes to the worktree, the task SHALL commit the changes or report a no-change result, and the runner SHALL verify `git status --porcelain` is clean before marking the task completed.

#### Scenario: Modified requirement is applied as added when source is absent
- **WHEN** `integrate:spec-sync` processes a `MODIFIED` requirement
- **AND** the main spec has no matching source requirement
- **AND** no rename maps to that source
- **AND** the target requirement name does not already exist
- **THEN** the sync SHALL add the requirement to the main spec
- **AND** the sync output SHALL record a correction from `modified` to `added` with capability, requirement, and reason

#### Scenario: Ambiguous or destructive deltas still fail
- **WHEN** `integrate:spec-sync` processes a missing-source `REMOVED` or `RENAMED FROM` requirement
- **THEN** the sync SHALL fail with structured conflict output
- **AND** it SHALL NOT silently delete, rename, or invent source requirements

#### Scenario: Spec-sync cannot complete with uncommitted changes

- **WHEN** `integrate:spec-sync` has written spec changes to the worktree
- **THEN** the task SHALL commit those changes or report that no changes were made
- **AND** the runner SHALL verify `git status --porcelain` is empty before reporting task completion
- **AND** if the worktree remains dirty the task SHALL fail with structured dirty-worktree evidence

### Requirement: Merge preflight validates clean worktree before delivery

The workflow engine SHALL ensure the merge action validates source worktree cleanliness before executing any delivery side effects. If the source worktree is dirty when merge starts, the merge SHALL fail before fetch, rebase, landing, or push operations begin.

#### Scenario: Dirty worktree blocks merge delivery

- **WHEN** the workflow engine dispatches `integrate:merge`
- **AND** the merge action detects a dirty source worktree
- **THEN** the merge SHALL fail with phase `source-cleanup`
- **AND** no fetch, rebase, landing, or push operations SHALL execute

#### Scenario: Merge phase failures produce distinct evidence

- **WHEN** `integrate:merge` fails
- **THEN** the task result SHALL include a `phase` field identifying the failure as `source-cleanup`, `fetch`, `rebase-conflict`, `landing-validation`, or `push`
- **AND** the WorkflowRun SHALL persist this phase classification as part of the task failure evidence

### Requirement: REQ-WFE-006 Post-sync main spec validation is mandatory

After intelligent sync resolves delta intent, the workflow engine SHALL validate the candidate main spec before writing or landing it. Invalid results, duplicate requirement headers, missing scenarios, malformed delta sections, or parse-back mismatches SHALL fail `integrate:spec-sync` with structured output.

#### Scenario: Invalid resolved spec is not written
- **WHEN** intelligent sync produces a candidate main spec with duplicate headers, missing scenarios, malformed structure, or parse-back mismatch
- **THEN** `integrate:spec-sync` SHALL fail
- **AND** the invalid result SHALL NOT be silently landed in the main specs
- **AND** the output SHALL include validation errors

### Requirement: ai-review task artifact contract

The workflow engine SHALL treat `ai-review` as the CHECK-stage task that produces the final review artifact for the current candidate snapshot. The task SHALL complete only when `review.md` exists, has the expected review format, contains a machine-readable verdict, and represents the current code snapshot.

#### Scenario: Missing review artifact fails ai-review task

- **WHEN** `ai-review` finishes execution
- **AND** `review.md` is missing after allowed retries
- **THEN** the `ai-review` task SHALL fail
- **AND** the workflow SHALL NOT create a separate user-visible check for missing review artifacts

#### Scenario: Unparseable verdict fails ai-review task

- **WHEN** `ai-review` produces `review.md`
- **AND** the verdict is missing or cannot be parsed
- **THEN** the `ai-review` task SHALL fail
- **AND** `review-passed` SHALL NOT be reported as the ordinary failing user-visible check for that artifact error

#### Scenario: Valid review artifact enables review-passed

- **WHEN** `ai-review` produces a valid final `review.md` with a parseable verdict
- **THEN** `review-passed` SHALL read that verdict as check evidence

### Requirement: review-passed dynamic repair

The workflow engine SHALL use `review-passed` as the read-only verifier for the final review verdict. When `review-passed` fails because the review verdict is FAIL, the engine SHALL create actual repair work from the review findings, rerun `ai-review`, and then rerun `review-passed` against the regenerated final review.

#### Scenario: Failed review creates actual repair task

- **WHEN** `review-passed` reads a FAIL verdict with repairable findings
- **THEN** the workflow SHALL create and run a concrete repair task based on those findings
- **AND** it SHALL NOT rely on a predeclared empty fix task that was visible before the failure occurred

#### Scenario: Repair invalidates old review

- **WHEN** review repair changes code or review-relevant artifacts
- **THEN** existing CHECK-stage review artifacts and review checkpoints SHALL be invalidated
- **AND** `ai-review` SHALL rerun before `review-passed` is evaluated again

#### Scenario: Re-review remains the approval truth

- **WHEN** repair is followed by a regenerated review
- **THEN** the regenerated review SHALL be the current review truth for approval
- **AND** stale review verdicts from earlier snapshots SHALL NOT be used for approval

### Requirement: Collect-first check phase reporting

The workflow engine SHALL run all checks in the current phase once in declared order before deciding how to handle failures. The initial phase result set SHALL preserve the complete diagnostic picture for the phase instead of stopping at the first non-pass result.

#### Scenario: Multiple ordinary failures are all visible

- **WHEN** a phase contains multiple non-approval checks
- **AND** more than one of those checks returns `fail` or `error`
- **THEN** the workflow SHALL record all initial check results from that phase run
- **AND** the user-visible phase result SHALL include the later failures instead of stopping at the first one only

#### Scenario: Baseline results are preserved before repair

- **WHEN** the initial phase run finds one or more failed or errored non-approval checks
- **THEN** the workflow SHALL persist the collected baseline check results before fix-task handling begins
- **AND** later rechecks MAY append newer results for the same check without erasing the original phase diagnosis

### Requirement: Approval pending remains non-repairable

`user-approval` SHALL remain a read-only check over existing approval state and SHALL NOT become a repair target. The workflow engine SHALL treat approval pending as a local awaiting-approval outcome only after ordinary non-approval failures have been cleared. When the user requests changes (feedback), the engine SHALL schedule an `apply-feedback` task as normal workflow work rather than treating the request as a check failure or repair target.

#### Scenario: Pending approval pauses without repair

- **WHEN** `user-approval` returns `pending`
- **AND** no non-approval check in the effective phase result set is failing or errored
- **THEN** the workflow SHALL stop in awaiting approval
- **AND** it SHALL NOT run a fix task for `user-approval`

#### Scenario: Approval does not mask ordinary failures

- **WHEN** `user-approval` returns `pending`
- **AND** another non-approval check in the same phase result set returns `fail` or `error`
- **THEN** the workflow SHALL treat the phase as a repair-or-fail path rather than awaiting approval
- **AND** it SHALL NOT request or refresh approval until the ordinary failures are resolved

#### Scenario: Requested changes schedules feedback task not repair task

- **WHEN** the user requests changes at an approval gate
- **THEN** the workflow engine SHALL schedule an `apply-feedback` task as normal workflow work
- **AND** the engine SHALL NOT map the feedback request to a check repair task
- **AND** the engine SHALL NOT mark the stage as failed
- **AND** the feedback task SHALL execute before checks rerun and approval is re-requested

### Requirement: REQ-WFE-001 Checks are read-only validators

Workflow checks SHALL be read-only validators. A check SHALL return fact evidence through `CheckResult` and SHALL NOT write durable artifacts, modify code, mutate git state, schedule repair work, advance stages, request approval, or update WorkflowRun state directly.

#### Scenario: Check returns evidence only

- **WHEN** a check runs
- **THEN** it SHALL return status, message, and optional output evidence
- **AND** it SHALL NOT perform code-changing, file-changing, git-changing, approval-changing, or stage-changing side effects

#### Scenario: Repair is modeled as a task

- **WHEN** a failed check is repairable by policy
- **THEN** WorkflowRun or StageRun SHALL schedule a task with causedBy metadata
- **AND** the check implementation SHALL NOT run the repair itself

### Requirement: REQ-WFE-002 Failed checks run explicit fix tasks by policy

Failed checks SHALL be handled by WorkflowRun/StageRun policy decisions. If a policy maps the failed check to a fix task, the aggregate SHALL schedule that task, require the runner to report its task result, and only then allow the relevant check to run again.

#### Scenario: Health check fix is visible

- **WHEN** `health:build` fails and has a `fix-build-health` policy
- **THEN** WorkflowRun SHALL append a `fix-build-health` task to the current StageRun
- **AND** it SHALL re-run `health:build` only after the fix task completes

#### Scenario: Max attempts stops current stage

- **WHEN** a failed check still fails after its configured fix attempts
- **THEN** WorkflowRun SHALL keep the failed check results and fix task results
- **AND** the current stage SHALL fail with a traceable check failure reason
- **AND** the workflow SHALL NOT escalate to another stage through a fallback chain

### Requirement: REQ-WFE-WORKFLOW-RUN-001 Workflow engine updates WorkflowRun runtime state

The workflow engine SHALL execute work requested by the active WorkflowRun aggregate and SHALL NOT decide next stage, stage pass/fail, awaiting approval, or workflow completion from runner-local `StageRunResult.nextStage` data. Stage lifecycle, task results, check results, approval snapshots, and terminal workflow status SHALL be decided by WorkflowRun and persisted transactionally before projection updates.

#### Scenario: Stage lifecycle is aggregate-decided

- **WHEN** a task or check result is reported
- **THEN** WorkflowRun SHALL decide whether the current StageRun remains running, awaits approval, completes, or fails
- **AND** the WorkflowEngine SHALL NOT directly mark the stage passed or failed

#### Scenario: Next stage comes from stage order

- **WHEN** the current StageRun completes
- **THEN** WorkflowRun SHALL derive the next stage from its stage order
- **AND** WorkflowEngine SHALL NOT use `StageRunResult.nextStage` to update issue stage

#### Scenario: Results update WorkflowRun before projections

- **WHEN** a runner records a task result, check result, or approval response
- **THEN** the matching WorkflowRun task, check, or approval snapshot SHALL be updated first
- **AND** `stage_executions`, `stage_states`, issue stage/status, and check suites MAY then be updated as projections or audit evidence

### Requirement: Stage execution infrastructure exposes shared stage side-effect helpers

Stage execution infrastructure SHALL expose shared stage-scoped safe `emit` and `log` helpers through `StageContext` or an equivalent shared stage runtime boundary.

#### Scenario: Runners reuse shared safe side-effect helpers

- **WHEN** Plan, Build, Check, or Integrate code needs to emit an existing workflow event or write a workflow log entry
- **THEN** it uses the shared stage-scoped helper instead of maintaining a runner-private `emitSafe` or `writeLog` implementation
- **AND** emitted event names and payload shapes remain unchanged
- **AND** workflow log event types and payload semantics remain unchanged

#### Scenario: Side-effect helper failures stay non-fatal

- **WHEN** the underlying event bus emit or workflow log insert throws or rejects
- **THEN** the shared helper swallows the infrastructure failure
- **AND** stage execution continues through the existing runner control flow

### Requirement: Static task loading is available for Plan Check and Integrate tasks

The workflow runtime SHALL support a static task loader that prepares executable Plan, Check, and Integrate tasks from `StageContext` without taking over Build or Ralph execution behavior.

#### Scenario: Static definitions resolve executable task input

- **WHEN** a static Plan, Check, or Integrate task definition is loaded
- **THEN** the loader resolves prompt or service-call input from `StageContext`
- **AND** it returns executable tasks in the same order as the supplied static definitions
- **AND** it does not introduce Build dynamic ordering, `dependsOn`, checkpoint logic, or Ralph task execution behavior

### Requirement: Legacy repair and fix entrypoints remain compatible through shared adapters

Legacy repair and fix task entrypoints SHALL remain available while dispatching through shared adapter-backed task execution.

#### Scenario: Shared adapter covers current repair and fix task ids

- **WHEN** the workflow executes an existing plan repair, review repair, merge repair, or stage health fix path
- **THEN** the legacy entrypoint resolves the real current task id through a shared adapter or registry-backed path
- **AND** the task executes through the shared handler contract appropriate for that task type
- **AND** compatibility exports such as `runHealthFixTask`, `runReviewFixTask`, and `runPlanRepairTask` remain available or have preserved equivalent entrypoints

### Requirement: Agent-session tasks share a reusable execution primitive

Agent-session-backed workflow tasks SHALL execute through a reusable `AgentSessionTaskHandler` execution primitive.

#### Scenario: Agent-session task normalizes execution outcomes

- **WHEN** a Plan or Check task, or an agent-backed repair task, executes through `AgentSessionTaskHandler`
- **THEN** the handler can report success, task failure, or retry-after-missing-artifact style results through normalized task output
- **AND** existing task-level events such as `stage_task_update` may still be emitted through the shared stage helper
- **AND** artifact verification or retry prompting remains scoped to the task execution boundary rather than stage progression

### Requirement: Service-backed workflow steps share a reusable execution primitive

Service-backed workflow tasks SHALL execute through a reusable `ServiceCallTaskHandler` execution primitive.

#### Scenario: Service-call task normalizes integrate and merge-style work

- **WHEN** an Integrate step or merge-style repair task invokes repository or application services through `ServiceCallTaskHandler`
- **THEN** the handler normalizes successful and failed service invocation results into `StageTaskResult`-style output
- **AND** the task continues to rely on the runner for stage-level events, checks, and final workflow decisions

### Requirement: Non-Build tasks execute through a minimal shared handler contract

Non-Build task execution SHALL support runtime-added rebase work through the same shared handler contract used by other WorkflowRun tasks. `rebase-branch` SHALL execute as ordinary WorkflowRun task work and SHALL NOT use a queue-only rebase execution path as the primary workflow behavior.

#### Scenario: Rebase task executes through normal workflow scheduling

- **WHEN** `WorkflowRun.nextWork()` returns `task: rebase-branch`
- **THEN** the workflow engine and stage runner SHALL execute that task through the shared task runtime
- **AND** later tasks or checks SHALL NOT run until `rebase-branch` reaches a terminal state

### Requirement: merge-ready invalidates review on code change

The workflow engine SHALL invalidate review, check, and approval state based on actual candidate snapshot change facts rather than on rebase intent alone. When a completed `rebase-branch` task reports `shaChanged=true`, the affected stage policy SHALL reset the dependent review/check state; when `shaChanged=false`, the prior review/check state MAY remain valid.

#### Scenario: Rebase with unchanged snapshot preserves review state

- **WHEN** `rebase-branch` completes successfully
- **AND** its result reports `shaChanged=false`
- **THEN** existing review/check state SHALL remain valid
- **AND** the workflow SHALL continue without forcing re-review solely because the user clicked Rebase

#### Scenario: Rebase with changed snapshot invalidates check-stage review truth

- **WHEN** `rebase-branch` completes successfully in Check stage
- **AND** its result reports `shaChanged=true`
- **THEN** the workflow SHALL invalidate `ai-review`, `review-passed`, `merge-ready`, and approval state for that stage
- **AND** later work SHALL re-run against the new snapshot before approval can be requested again

#### Scenario: Failed rebase blocks later work

- **WHEN** `rebase-branch` fails
- **THEN** the current stage SHALL fail through normal task failure semantics
- **AND** later tasks or checks SHALL NOT execute

### Requirement: Check merge-ready uses squash merge semantics

The Check-stage `merge-ready` gate SHALL pass only when the current issue candidate can be squash-merged into the current base branch using Mohist's final Integrate merge semantics.

#### Scenario: Conflicting squash merge fails merge-ready

- **GIVEN** an issue candidate whose normal issue worktree has no active rebase conflict files
- **AND** the same candidate would fail `git merge --squash <candidate>` against the current base branch
- **WHEN** the Check stage runs `merge-ready`
- **THEN** `merge-ready` SHALL fail
- **AND** the output SHALL include structured mergeability facts including `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `targetBranch`, `strategy`, `canMerge`, and `conflictFiles`

#### Scenario: Clean squash merge passes merge-ready

- **GIVEN** an issue candidate that can be cleanly squash-merged into the current base branch
- **WHEN** the Check stage runs `merge-ready`
- **THEN** `merge-ready` SHALL pass
- **AND** the pass decision SHALL be based on `canMerge: true` from the squash mergeability preflight

### Requirement: Check approval validates mergeability snapshot freshness

Check approval SHALL be bound to the passing merge-ready snapshot that was presented for approval and SHALL reject stale or missing mergeability evidence before enqueueing Integrate.

#### Scenario: Approval rejects stale merge-ready evidence

- **GIVEN** Check produced a passing `mergeReadySnapshot`
- **AND** the base branch, candidate head, merge base, target branch, or `canMerge` value no longer matches the current Git state
- **WHEN** the user approves Check
- **THEN** Mohist SHALL reject the approval as stale
- **AND** Mohist SHALL ask for Check to be rerun instead of approving a different candidate than the one presented

#### Scenario: Approval rejects missing merge-ready evidence

- **GIVEN** Check approval output has no valid passing `mergeReadySnapshot`
- **WHEN** the user approves Check
- **THEN** Mohist SHALL reject the approval before enqueueing Integrate

### Requirement: Integrate preflights before side effects

Integrate SHALL validate or refresh mergeability evidence before running side-effectful delivery steps such as spec sync, change archive, or the final merge.

#### Scenario: Integrate stops before side effects on stale evidence

- **GIVEN** approved mergeability evidence is missing or stale when Integrate starts
- **WHEN** Integrate validates mergeability before delivery side effects
- **THEN** Integrate SHALL stop before spec sync, archive, or merge side effects if the current candidate cannot be proven mergeable
- **AND** the failure SHALL include structured mergeability evidence or a clear instruction to rerun Check

#### Scenario: Integrate continues with current mergeability evidence

- **GIVEN** approved mergeability evidence still matches current base and candidate state
- **WHEN** Integrate starts
- **THEN** Integrate SHALL continue to existing delivery steps without adding a new user-facing workflow status

### Requirement: REQ-BDA-EVIDENCE-001 Check approval rejects stale base evidence

The workflow engine SHALL prevent Check approval from being requested or accepted when base drift makes review, merge-ready, or approval evidence stale.

#### Scenario: Drift invalidates Check approval evidence

- **WHEN** base drift is detected for an active Check issue
- **AND** the current approval evidence references an older base, merge base, or candidate snapshot
- **THEN** Check approval SHALL no longer be actionable
- **AND** Mohist SHALL instruct the user to rebase or rerun Check before approval can proceed

#### Scenario: Approval submit race is rejected

- **WHEN** a user submits Check approval
- **AND** the approval evidence is stale because the base advanced
- **THEN** Mohist SHALL reject the approval
- **AND** the issue SHALL NOT advance to Integrate from that stale evidence

#### Scenario: Rebase completion refreshes dependent evidence

- **WHEN** `rebase-branch` completes and changes candidate or base evidence
- **THEN** Check review, merge-ready, and approval state SHALL be invalidated or reset
- **AND** the affected evidence SHALL be regenerated before approval can be requested again

### Requirement: REQ-BDA-SAFE-WINDOW-001 Mutating work is protected from automatic rebase

The workflow engine SHALL only schedule automatic drift-driven rebase when the current WorkflowRun is at a safe window.

#### Scenario: Running mutating work defers rebase

- **WHEN** base drift is detected during a running mutating task
- **THEN** Mohist SHALL NOT schedule `rebase-branch` immediately
- **AND** the rebase opportunity SHALL record a defer reason

#### Scenario: Task boundary reconsiders deferred opportunity

- **WHEN** a mutating task completes after drift was deferred
- **THEN** Mohist SHALL re-evaluate the rebase opportunity
- **AND** the opportunity SHALL become suggestible or schedulable if the new state is a safe window

### Requirement: Check full verification before review

The workflow engine SHALL run Check full verification before generating or reusing AI review as the current approval candidate. A failing or missing verification result SHALL stop Check before AI review, merge-ready, or user approval.

#### Scenario: Verification runs before AI review

- **WHEN** default Check execution starts for a candidate implementation
- **THEN** the system SHALL run the configured full verification gate before `ai-review`
- **AND** it SHALL NOT generate a new AI review before verification passes

#### Scenario: Verification failure blocks later Check work

- **WHEN** Check full verification fails
- **THEN** Check SHALL NOT run `ai-review`
- **AND** Check SHALL NOT run `merge-ready`
- **AND** Check SHALL NOT request user approval

#### Scenario: Verification pass allows review and mergeability

- **WHEN** Check full verification passes
- **THEN** Check MAY continue to `ai-review`, `review-passed`, `merge-ready`, and approval gating for the same candidate implementation

### Requirement: Check approval requires current verification

Check approval SHALL only be requested when full verification, AI review, and merge-ready evidence all pass for the same current candidate implementation.

#### Scenario: Missing verification blocks approval request

- **WHEN** Check reaches approval gating
- **AND** no current passing `health:check` evidence exists
- **THEN** the system SHALL NOT request Check approval
- **AND** it SHALL expose a blocking reason that verification evidence is missing

#### Scenario: Stale verification blocks approval request

- **WHEN** Check reaches approval gating
- **AND** passing verification evidence does not match the current approval candidate snapshot
- **THEN** the system SHALL NOT request Check approval
- **AND** it SHALL require Check verification to rerun for the current candidate

#### Scenario: Approval candidate includes verification evidence

- **WHEN** Check approval is requested
- **THEN** the approval candidate output SHALL include verification evidence, review verdict evidence, and merge-ready evidence
- **AND** all included evidence SHALL refer to the same candidate implementation

### Requirement: Config-driven runner executes declared stage work

The workflow engine SHALL provide a config-driven stage runner path that executes tasks, checks, approvals, repairs, and invalidations from stage definitions and registries. The runner SHALL orchestrate work requested by WorkflowRun and SHALL NOT duplicate WorkflowRun stage progression decisions in stage-specific subclasses.

#### Scenario: Runner executes requested task from registries

- **WHEN** `WorkflowRun.nextWork()` returns a task for the current stage
- **THEN** the config-driven runner SHALL resolve the executable task from the stage definition work sources and task loader registry
- **AND** it SHALL execute the task through the handler selected by the task execution policy
- **AND** it SHALL report the task result back to WorkflowRun before selecting later work

#### Scenario: Runner executes requested check from registry

- **WHEN** `WorkflowRun.nextWork()` returns a check for the current stage
- **THEN** the config-driven runner SHALL resolve the check from the stage definition check policy and check registry
- **AND** it SHALL report the check result back to WorkflowRun before selecting later work

#### Scenario: Runner does not decide stage progression

- **WHEN** a task, check, repair task, or approval result is reported
- **THEN** WorkflowRun SHALL decide whether the stage continues, awaits approval, passes, or fails
- **AND** the config-driven runner SHALL NOT derive the next stage from runner-local stage result data

### Requirement: Legacy and config-driven runner paths coexist during migration

The stage execution infrastructure SHALL keep the legacy runner path available while each stage is migrated to config-driven execution. The system SHALL NOT delete legacy Plan, Build, Check, or Integrate runner files or remove the rollback path in this change.

#### Scenario: Unmigrated stage can use legacy runner path

- **WHEN** a stage has not yet been enabled for config-driven execution
- **THEN** the workflow engine SHALL be able to execute that stage through the existing legacy runner path
- **AND** existing task, check, repair, approval, checkpoint, event, and log behavior SHALL remain available

#### Scenario: Migrated stage uses config-driven path independently

- **WHEN** one stage is enabled for config-driven execution and another stage is still legacy
- **THEN** the enabled stage SHALL execute through the config-driven path
- **AND** the legacy stage SHALL remain executable through the legacy path

#### Scenario: Unified runner becomes default only after all stages migrate

- **WHEN** Integrate, Plan, Check, and Build have each passed their config-driven validation
- **THEN** WorkflowEngine SHALL use the unified config-driven stage runner as the default runner for those stages
- **AND** the legacy runner files SHALL remain present as rollback implementation during this issue

### Requirement: Config-driven checks preserve read-only and repair policy boundaries

The config-driven runner SHALL execute checks as read-only validators and SHALL schedule repair tasks only through WorkflowRun or StageRun repair policy decisions.

#### Scenario: Failed check schedules configured repair task

- **WHEN** a config-driven check fails and the stage repair policy allows a repair task for that check
- **THEN** WorkflowRun or StageRun SHALL append the configured repair task with causedBy metadata
- **AND** the check implementation SHALL NOT run the repair itself
- **AND** the runner SHALL execute the repair task as ordinary task work before the relevant check is re-evaluated

#### Scenario: Approval remains a user decision point

- **WHEN** ordinary non-approval checks have passed and the stage requires approval
- **THEN** WorkflowRun SHALL expose an approval wait state rather than treating approval as a repairable check failure
- **AND** the runner SHALL preserve existing approval output semantics for Plan and Check

### Requirement: Config-driven invalidation applies branch and repair facts

The config-driven workflow path SHALL apply invalidation policies from stage definitions after task results report facts that make prior task, check, or approval state stale.

#### Scenario: Review repair invalidates stale review state

- **WHEN** a Check-stage repair task changes code or review-relevant artifacts
- **THEN** the configured invalidation policy SHALL reset stale AI review, review-passed, merge-ready, and approval state before approval can be requested again

#### Scenario: Rebase facts drive invalidation

- **WHEN** `rebase-branch` completes and reports branch facts
- **THEN** the configured invalidation policy SHALL decide whether dependent tasks, checks, or approval state are invalidated based on those facts
- **AND** a rebase that does not change the candidate snapshot SHALL NOT force re-review solely because the rebase task ran

### Requirement: Aggregate single-work execution remains supported

The workflow engine SHALL preserve aggregate single task and single check execution behavior when using the config-driven runner.

#### Scenario: Aggregate requested task executes once

- **WHEN** aggregate workflow execution invokes a runner for one requested task
- **THEN** the config-driven runner SHALL execute only that task
- **AND** it SHALL report exactly that task result before WorkflowRun computes the next work item

#### Scenario: Aggregate requested check executes once

- **WHEN** aggregate workflow execution invokes a runner for one requested check
- **THEN** the config-driven runner SHALL execute only that check
- **AND** it SHALL report exactly that check result before WorkflowRun computes the next work item

### Requirement: Workflow runners materialize or report required work before completion

Workflow runners SHALL materialize required work into StageRun evidence or record a recoverable blocked/failure reason before asking WorkflowRun to complete a stage.

#### Scenario: Explicit completion uses the domain guard
- **WHEN** a runner reaches the end of its local execution loop
- **THEN** it SHALL call the WorkflowRun completion decision used by `nextWork()`
- **AND** it SHALL surface the returned blocked reason instead of advancing the stage directly

#### Scenario: Build runner records dynamic source outcome
- **WHEN** Build evaluates `tasks.json`
- **THEN** the runner SHALL record whether the source was evaluated successfully, missing, invalid, or empty
- **AND** successful evaluation SHALL materialize generated tasks as StageRun TaskRun records before task execution or completion decisions

#### Scenario: Runtime work invalidates dependent checks
- **WHEN** a runner appends runtime work that can change the candidate or stage facts
- **THEN** it SHALL preserve reason or causedBy metadata on the appended TaskRun
- **AND** it SHALL invalidate or replace dependent checks and approval evidence according to policy before completion can continue

#### Scenario: Check and Integrate runners record authoritative evidence
- **WHEN** Check or Integrate succeeds
- **THEN** the runner SHALL persist the current task/check and delivery evidence required by WorkflowRun
- **AND** it SHALL NOT rely on AgentSession status or merge state alone to request Done

### Requirement: Workflow execution records work item attempts

The workflow engine SHALL record task and check execution through WorkflowRun work item attempt transitions. Execution SHALL start a running attempt before dispatching a work item and SHALL complete, fail, or interrupt that attempt according to the actual execution outcome.

#### Scenario: Build task execution records attempts

- **WHEN** Build dispatches a task work item
- **THEN** the matching task SHALL start a `running` latest attempt before work is dispatched
- **AND** the attempt SHALL become `completed` or `failed` when the task result is known

#### Scenario: Check execution records attempts

- **WHEN** a stage dispatches a check work item
- **THEN** the matching check SHALL start a `running` latest attempt before the check is run
- **AND** the attempt SHALL become `completed` or `failed` when the check result is known

#### Scenario: Genuine execution failure remains failed

- **WHEN** a task or check handler returns a genuine failed result or error result
- **THEN** the latest work item attempt SHALL become `failed`
- **AND** retry eligibility MAY be derived from that failed latest attempt

### Requirement: Stopped or lost execution interrupts attempts

The workflow engine SHALL distinguish stopped or lost execution from failed work results. Intentional stop, cancelled session state, lost process, or stale running evidence SHALL mark the related running work item attempt interrupted unless a genuine failed work result exists.

#### Scenario: Intentional agent stop interrupts current work

- **WHEN** Mohist intentionally stops an agent that is executing a workflow work item
- **THEN** the related coder session SHALL be marked cancelled or interrupted
- **AND** the current work item's latest attempt SHALL become `interrupted` with diagnostic reason
- **AND** historical stop evidence SHALL remain visible for inspection

#### Scenario: Lost execution does not become failed

- **WHEN** a running agent process or session disappears without a failed task or check result
- **THEN** the latest attempt SHALL become `interrupted`
- **AND** the system SHALL NOT expose the work as retryable failed work solely because execution was lost

### Requirement: Reconcile stale running attempts before recovery decisions

The workflow engine SHALL reconcile the current work item's latest `running` attempt against live execution evidence before recovery-sensitive reads, writes, and workflow resume decisions.

#### Scenario: Live evidence keeps attempt running

- **WHEN** the latest attempt is `running`
- **AND** an active queue task, live related coder session, or live related agent process proves execution is still active
- **THEN** reconciliation SHALL leave the attempt `running`
- **AND** recovery guidance SHALL be wait or stop

#### Scenario: Missing evidence interrupts attempt

- **WHEN** the latest attempt is `running`
- **AND** there is no running or pending queue task and no live related session or process evidence
- **THEN** reconciliation SHALL idempotently mark the attempt `interrupted`
- **AND** it SHALL record an interruption reason such as `agent-stopped` or `agent-lost`
- **AND** the workflow summary SHALL move to waiting for recovery

#### Scenario: Reconciliation is invoked on recovery paths

- **WHEN** issue detail, stage-state, queue recovery, retry availability, resume, rerun, CLI status, or workflow resume code evaluates primary recovery actions
- **THEN** it SHALL use the reconciled latest attempt state before exposing or accepting those actions

### Requirement: REQ-WE-001 workflow engine converges through structured reactions

The workflow engine SHALL use structured failed context and verification-mode rechecks to converge after check failures while preserving task/check boundaries.

#### Scenario: Check failure schedules a reaction task

- **WHEN** a read-only check fails because parsed structured output contains blocking current-change items
- **THEN** the engine SHALL schedule the configured reaction task according to workflow policy
- **AND** the reaction task SHALL receive the full relevant blocking item batch
- **AND** the check itself SHALL NOT start agents, modify artifacts, or repair files

#### Scenario: Verification mode evaluates known items first

- **WHEN** a reaction task has attempted repairs
- **THEN** the engine SHALL re-run the configured task/check path in verification mode with known item IDs and expected repairs
- **AND** unresolved known blockers or policy-allowed new blockers SHALL keep the stage blocked with structured evidence
- **AND** a reaction task SHALL NOT directly mutate a failed check into pass without recheck evidence

#### Scenario: Existing review history remains compatible

- **WHEN** structured review convergence is enabled
- **THEN** existing review history behavior and reviewed-snapshot binding SHALL remain compatible and SHALL NOT be replaced by review-specific core domain state

### Requirement: Workflow engine schedules apply-feedback as normal task

The workflow engine SHALL treat `apply-feedback` as an ordinary agent-session-backed workflow task. The task SHALL execute through the same shared task execution primitives used by other agent-session tasks. The engine SHALL NOT create a special feedback-only execution path.

#### Scenario: apply-feedback executes through AgentSessionTaskHandler

- **WHEN** the engine dispatches `apply-feedback`
- **THEN** it SHALL resolve the task through the configured task execution policy
- **AND** agent-session-backed execution SHALL use the same `AgentSessionTaskHandler` used by other agent tasks
- **AND** task result reporting SHALL use normal task result semantics

#### Scenario: Feedback task dispatch includes approvalFeedback context

- **WHEN** the engine dispatches `apply-feedback`
- **THEN** the dispatch context SHALL include the `approvalFeedback` object with id, stage, summary, and CLI command
- **AND** the prompt SHALL be rendered from the configured prompt source (built-in or custom)
- **AND** the prompt SHALL include the CLI read command for the full feedback body

### Requirement: Feedback loop reruns checks before re-approval

After the `apply-feedback` task completes successfully, the workflow engine SHALL rerun the configured stage checks before requesting approval again. The engine SHALL NOT request approval while feedback-driven changes remain unvalidated.

#### Scenario: Checks rerun after successful feedback task

- **WHEN** `apply-feedback` completes successfully
- **THEN** the engine SHALL invalidate prior check and approval evidence for the stage
- **AND** the engine SHALL rerun the stage checks in their configured order
- **AND** approval SHALL only be requested after all checks pass

#### Scenario: Failed check after feedback blocks re-approval

- **WHEN** checks rerun after `apply-feedback` completes
- **AND** one or more checks fail
- **THEN** the engine SHALL enter the normal check failure repair path
- **AND** approval SHALL NOT be requested until failures are resolved
- **AND** the feedback SHALL remain resolved regardless of subsequent check failures
