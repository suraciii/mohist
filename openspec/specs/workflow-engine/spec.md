# OpenSpec Capability: workflow-engine

### Requirement: Stage enum contains only M1 stages

The `Stage` enum SHALL contain only values used by M1: `Draft`, `Designing`, `Implementing`, `Done`. The values `WaitingDesignReview` and `WaitingReview` SHALL be removed.

#### Scenario: Stage enum values
- **WHEN** the Stage enum is inspected
- **THEN** it SHALL contain exactly 4 values: `draft`, `designing`, `implementing`, `done`
- **AND** it SHALL NOT contain `waiting-design-review` or `waiting-review`

### Requirement: Task infrastructure is removed

The `Task` interface SHALL be removed from `types/index.ts`. The `TaskRepo` class SHALL be deleted. The `tasks` SQLite table SHALL be dropped.

#### Scenario: No Task type
- **WHEN** the types module is inspected
- **THEN** it SHALL NOT export a `Task` interface

#### Scenario: No TaskRepo
- **WHEN** the db module is inspected
- **THEN** it SHALL NOT export `TaskRepo`

#### Scenario: Tasks table dropped
- **WHEN** the server starts and initializes the database
- **THEN** the `tasks` table SHALL NOT exist

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

The workflow engine and issue-bound recovery sessions SHALL resolve coder models with issue-level overrides before global configuration. The fallback order SHALL be `issue.stageModels[stage]`, then `issue.model`, then `config.opencode.stageModels[stage]`, then `config.opencode.model`, then opencode default.

#### Scenario: Issue stage model overrides all lower levels

- **WHEN** an issue has `stageModels.build = "anthropic/claude-opus-4-20250514"`
- **AND** the issue has `model = "anthropic/claude-sonnet-4-20250514"`
- **AND** global build/default models are configured
- **THEN** the build-stage coder session uses `"anthropic/claude-opus-4-20250514"`

#### Scenario: Issue default model applies when stage override is unset

- **WHEN** an issue has `model = "openai/gpt-4o"`
- **AND** no issue stage model exists for the current stage
- **THEN** the coder session uses `"openai/gpt-4o"`
- **AND** global stage/default models are ignored

#### Scenario: Global configuration remains fallback

- **WHEN** an issue has no issue-level model metadata
- **AND** global stage or default model configuration exists
- **THEN** the coder session uses the existing global model resolution behavior

#### Scenario: Recovery sessions use build-stage policy

- **WHEN** conflict resolution or build-error-fix starts an issue-bound coder session
- **THEN** the session resolves its model using build-stage policy plus the issue-level overrides

### Requirement: REQ-WFE-005 Intelligent spec sync resolves obvious delta classification mistakes

The workflow engine SHALL provide an intelligent OpenSpec sync path for `integrate:spec-sync` that can absorb obvious requirement-level delta classification mistakes while preserving strict validation. At minimum, when a `MODIFIED` requirement has no matching source requirement in the main spec, has no rename ambiguity, and does not duplicate an existing target requirement, the sync path SHALL apply it as an added requirement and record the correction.

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

`user-approval` SHALL remain a read-only check over existing approval state and SHALL NOT become a repair target. The workflow engine SHALL treat approval pending as a local awaiting-approval outcome only after ordinary non-approval failures have been cleared.

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

#### Scenario: Rejected approval is not repairable

- **WHEN** `user-approval` returns `fail` because approval state is rejected
- **THEN** the workflow SHALL keep that result as visible evidence
- **AND** it SHALL NOT map the approval rejection to a check repair task

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

