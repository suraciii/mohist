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

### Requirement: REQ-WFE-001 Checks are read-only validators

Workflow checks SHALL be read-only validators. A check SHALL expose a name and `run(ctx)` behavior that returns a `CheckResult`, and SHALL NOT expose or implement a fix method, spawn coder agents, write durable artifacts, modify code, advance stages, or re-run tasks.

#### Scenario: Check interface has no execution behavior
- **WHEN** the workflow check interface is inspected
- **THEN** it SHALL define check identity and `run(ctx)` verification behavior
- **AND** it SHALL NOT require `reaction`
- **AND** it SHALL NOT include `fix?()`

#### Scenario: Check failure records evidence only
- **WHEN** a check fails
- **THEN** the check SHALL return `CheckResult` with `status`, optional `message`, and optional transient `output`
- **AND** the check SHALL NOT directly start repair work

### Requirement: REQ-WFE-002 Failed checks run explicit fix tasks by policy

The workflow engine SHALL handle a failed check through a stage-local check failure policy. If a policy maps the failed check to a fix task, the engine SHALL run that fix task, persist its task result, re-run the failed check, and stop the current stage after the configured max attempts.

#### Scenario: Health check fix is visible
- **WHEN** `health:build` fails and has a `fix-build-health` policy
- **THEN** the workflow SHALL append a `fix-build-health` task result
- **AND** it SHALL re-run `health:build` after the fix task completes

#### Scenario: Max attempts stops current stage
- **WHEN** a failed check still fails after its configured fix attempts
- **THEN** the workflow SHALL keep the failed check results and fix task results
- **AND** the current stage SHALL fail or pause
- **AND** the workflow SHALL NOT escalate to another stage through a fallback chain

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

### Requirement: merge-ready invalidates review on code change

The workflow engine SHALL use `merge-ready` as the read-only user-visible verifier that the reviewed candidate can be integrated into the target branch. If merge-readiness work changes the candidate code snapshot, the workflow SHALL invalidate the existing review result and rerun `ai-review` before approval.

#### Scenario: Merge-ready passes without snapshot change

- **WHEN** the reviewed candidate can be merged into the target branch without changing the candidate snapshot
- **THEN** `merge-ready` SHALL pass
- **AND** the current `review-passed` result MAY remain valid for approval

#### Scenario: Merge-ready records mergeability failure

- **WHEN** the reviewed candidate cannot currently be merged into the target branch
- **THEN** `merge-ready` SHALL fail with target branch and conflict or mergeability evidence
- **AND** the workflow SHALL NOT expose the legacy `merge-readiness` check name for that decision

#### Scenario: Merge repair changes snapshot

- **WHEN** merge-readiness repair, rebase, or conflict resolution changes `HEAD`
- **THEN** the current review result SHALL be invalidated
- **AND** `ai-review` SHALL rerun for the new snapshot
- **AND** approval SHALL NOT be requested until `review-passed` and `merge-ready` both pass for that snapshot

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

