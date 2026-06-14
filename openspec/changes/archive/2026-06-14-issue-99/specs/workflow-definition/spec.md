# OpenSpec Capability: workflow-definition

## MODIFIED Requirements

### Requirement: REQ-WD-002 Integrate uses the standard task/check stage contract

The Integrate stage SHALL execute deterministic integration work as standard WorkflowRun tasks and SHALL run final verification as a read-only WorkflowRun check. Integrate ordering, task failure handling, merge delivery metadata, freeze behavior, and post-merge health failure handling SHALL be decided by StageRun rather than by runner-local step state.

#### Scenario: Integrate runs tasks before checks

- **WHEN** an issue enters Integrate
- **THEN** the stage SHALL execute `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, and `integrate:push` as ordered StageRun tasks
- **AND** it SHALL run `health:integrate` only after those tasks succeed

#### Scenario: Integrate failure stays local

- **WHEN** `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, or `integrate:push` fails
- **THEN** later Integrate tasks and checks SHALL NOT run
- **AND** the issue SHALL remain associated with Integrate failure evidence

#### Scenario: Post-merge health cannot auto-fix

- **WHEN** `health:integrate` fails after merge has completed
- **THEN** the failure SHALL be recorded as a post-merge delivery failure
- **AND** the stage SHALL NOT apply any check failure policy that would modify code after the merge freeze point

### Requirement: REQ-WD-001 Integrate owns intelligent OpenSpec spec sync

The workflow SHALL treat `integrate:spec-sync` as the stage task that writes approved change delta specs into main OpenSpec specs. The task SHALL read the change delta specs and existing main specs, resolve clear ADDED, MODIFIED, REMOVED, and RENAMED intent, and preserve separate integration steps for spec sync, archive, merge, push, and `health:integrate`.

#### Scenario: Integrate runs distinct ordered steps
- **WHEN** an approved change enters INTEGRATE
- **THEN** the workflow SHALL run `integrate:spec-sync` before `integrate:archive-change`
- **AND** it SHALL keep `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, `integrate:push`, and `health:integrate` as distinct task or step history entries

#### Scenario: Archive waits for spec sync
- **WHEN** `integrate:spec-sync` fails
- **THEN** the workflow SHALL NOT archive the OpenSpec change
- **AND** it SHALL NOT merge the candidate, push the base branch, or run `health:integrate`

### Requirement: Stage definitions preserve existing stage semantics

The declarative definitions for Plan, Build, Check, and Integrate SHALL preserve the existing user-visible workflow semantics while moving stage differences into configuration and registries.

#### Scenario: Integrate definition preserves integration contract

- **WHEN** Integrate executes through the config-driven runner
- **THEN** it SHALL execute spec sync, change archive, branch merge, and remote push as ordered stage tasks
- **AND** it SHALL run the Integrate health check only after those tasks succeed

## ADDED Requirements

### Requirement: Integrate stage declares integrate:push as the final delivery task

The built-in workflow definition SHALL declare `integrate:push` as a required Integrate stage task that runs immediately after `integrate:merge` and before any post-merge health check.

#### Scenario: Default workflow includes push task
- **WHEN** the system loads the built-in default workflow
- **THEN** the Integrate stage tasks SHALL include `integrate:push`
- **AND** `integrate:push` SHALL be ordered after `integrate:merge`
- **AND** `integrate:push` SHALL be ordered before `health:integrate`
