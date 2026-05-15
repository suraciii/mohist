## ADDED Requirements

### Requirement: Stage definitions declare workflow behavior policies

The built-in workflow definition SHALL describe each runnable stage with declarative stage configuration that includes its default tasks, work sources, task execution policy, check policy, approval policy, repair policy, and invalidation policy. The stage definition SHALL describe the shape and policies of the stage and SHALL NOT execute tasks, run checks, mutate artifacts, mutate git state, or decide stage transitions directly.

#### Scenario: Default stages expose declarative policies

- **WHEN** the system loads the built-in workflow definition
- **THEN** the Plan, Build, Check, and Integrate stage definitions SHALL each expose the work sources, task execution policy, check policy, approval policy when applicable, repair policy when applicable, and invalidation policy needed to execute that stage
- **AND** the declared stage order SHALL remain `plan -> build -> check -> integrate -> done`

#### Scenario: Stage definition remains non-executing

- **WHEN** code inspects a stage definition
- **THEN** the definition SHALL contain data needed to bind work sources, handlers, checks, approvals, repairs, and invalidations
- **AND** it SHALL NOT perform the work described by that data

### Requirement: Stage definitions bind to task and check registries

The workflow definition SHALL identify task work by source and execution policy, and check work by check policy, so a generic stage runner can resolve executable work through task loader, task handler, and check registries without stage-specific private branching.

#### Scenario: Static non-Build work resolves from definition

- **WHEN** a Plan, Check, or Integrate stage definition declares static tasks
- **THEN** the workflow runtime SHALL resolve those tasks through the static task loading path
- **AND** it SHALL execute them through the task handler selected by the task execution policy

#### Scenario: Checks resolve from check policy

- **WHEN** a stage definition declares pre-task, post-task, or approval checks
- **THEN** the generic stage runner SHALL resolve those checks through the configured check registry
- **AND** the checks SHALL run in the order and phase declared by the stage definition

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
- **THEN** it SHALL execute spec sync, change archive, and branch merge as ordered stage tasks
- **AND** it SHALL run the Integrate health check only after those tasks succeed
