## MODIFIED Requirements

### Requirement: REQ-WD-001 Integrate owns intelligent OpenSpec spec sync

The workflow SHALL treat `integrate:spec-sync` as the stage task that writes approved change delta specs into main OpenSpec specs. The task SHALL read the change delta specs and existing main specs, resolve clear ADDED, MODIFIED, REMOVED, and RENAMED intent, and preserve separate integration steps for spec sync, archive, delivery (prepare then publish), and final health.

#### Scenario: Integrate runs distinct ordered steps
- **WHEN** an approved change enters INTEGRATE
- **THEN** the workflow SHALL run `integrate:spec-sync` before `integrate:archive-change`
- **AND** it SHALL run `integrate:prepare` before `integrate:publish`
- **AND** it SHALL keep `integrate:spec-sync`, `integrate:archive-change`, `integrate:prepare`, `integrate:publish`, and `final-health` as distinct task or step history entries

#### Scenario: Archive waits for spec sync
- **WHEN** `integrate:spec-sync` fails
- **THEN** the workflow SHALL NOT archive the OpenSpec change
- **AND** it SHALL NOT prepare or publish the candidate or run final health

### Requirement: REQ-WD-002 Integrate uses the standard task/check stage contract

The Integrate stage SHALL execute deterministic integration work as standard WorkflowRun tasks and SHALL run final verification as a read-only WorkflowRun check. Integrate ordering, task failure handling, delivery metadata (prepare and publish), freeze behavior, and post-publish health failure handling SHALL be decided by StageRun rather than by runner-local step state.

#### Scenario: Integrate runs tasks before checks

- **WHEN** an issue enters Integrate
- **THEN** the stage SHALL execute `integrate:spec-sync`, `integrate:archive-change`, `integrate:prepare`, and `integrate:publish` as ordered StageRun tasks
- **AND** it SHALL run `health:integrate` only after those tasks succeed

#### Scenario: Integrate failure stays local

- **WHEN** `integrate:spec-sync`, `integrate:archive-change`, `integrate:prepare`, or `integrate:publish` fails
- **THEN** later Integrate tasks and checks SHALL NOT run
- **AND** the issue SHALL remain associated with Integrate failure evidence

#### Scenario: Post-publish health cannot auto-fix

- **WHEN** `health:integrate` fails after `integrate:publish` has completed
- **THEN** the failure SHALL be recorded as a post-publish delivery failure
- **AND** the stage SHALL NOT apply any check failure policy that would modify code after the publish freeze point

### Requirement: Stage definitions preserve existing stage semantics

The declarative definitions for Plan, Build, Check, and Integrate SHALL preserve the existing user-visible workflow semantics while moving stage differences into configuration and registries.

#### Scenario: Plan definition preserves planning contract

- **WHEN** Plan executes through the config-driven runner
- **THEN** it SHALL generate proposal, specs, design, tasks, and self-review work as Plan stage tasks
- **AND** it SHALL retain Plan approval, artifact validation checks, health check behavior, and checkpoint compatibility

#### Scenario: Check definition preserves review contract

- **WHEN** Check executes through the config-driven runner
- **THEN** it SHALL execute AI review as stage work before review and merge readiness checks
- **AND** it SHALL retain user approval, repair policy, stale review invalidation, and merge readiness behavior

#### Scenario: Build definition preserves Ralph contract

- **WHEN** Build executes through the config-driven runner
- **THEN** it SHALL consume Ralph dynamic tasks as Build stage tasks
- **AND** it SHALL retain checkpoint resume, task materialization, aggregate single task execution, and health gate repair behavior

#### Scenario: Integrate definition preserves integration contract

- **WHEN** Integrate executes through the config-driven runner
- **THEN** it SHALL execute spec sync, change archive, branch prepare, and branch publish as ordered stage tasks
- **AND** it SHALL run the Integrate health check only after those tasks succeed
