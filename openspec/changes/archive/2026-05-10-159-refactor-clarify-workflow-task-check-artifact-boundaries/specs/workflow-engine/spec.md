## ADDED Requirements

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
