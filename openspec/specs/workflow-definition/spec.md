# OpenSpec Capability: workflow-definition

### Requirement: REQ-WD-001 Integrate owns intelligent OpenSpec spec sync

The workflow SHALL treat `integrate:spec-sync` as the stage task that writes approved change delta specs into main OpenSpec specs. The task SHALL read the change delta specs and existing main specs, resolve clear ADDED, MODIFIED, REMOVED, and RENAMED intent, and preserve separate integration steps for spec sync, archive, delivery (rebase then push), and final health. The task SHALL commit generated spec changes to the worktree or report a no-change result before completing; the runner SHALL verify `git status --porcelain` is clean before marking the task completed.

#### Scenario: Integrate runs distinct ordered steps

- **WHEN** an approved change enters INTEGRATE
- **THEN** the workflow SHALL run `integrate:spec-sync` before `integrate:archive-change`
- **AND** it SHALL run `integrate:rebase` before `integrate:push`
- **AND** it SHALL keep `integrate:spec-sync`, `integrate:archive-change`, `integrate:rebase`, `integrate:push`, and `final-health` as distinct task or step history entries

#### Scenario: Archive waits for spec sync

- **WHEN** `integrate:spec-sync` fails
- **THEN** the workflow SHALL NOT archive the OpenSpec change
- **AND** it SHALL NOT rebase, push, or run final health

#### Scenario: Spec-sync commits or reports no-change before completing

- **WHEN** `integrate:spec-sync` generates or copies spec changes into the worktree
- **THEN** the task SHALL commit the changes or report that no changes were made
- **AND** the runner SHALL verify `git status --porcelain` is clean before marking the task completed

### Requirement: default runnable workflow stages match executed pipeline (REQ-001)

The built-in default workflow definition SHALL only declare stages that the runner can actually execute.

#### Scenario: Explore is not declared as a default runnable stage
- **WHEN** the system loads the built-in default workflow
- **THEN** the declared runnable stages do not include `explore`

#### Scenario: Default runnable workflow matches execution order
- **WHEN** no project-specific workflow overrides are present
- **THEN** the default runnable stages match the order declared in `mohist-default.workflow.yaml`
- **AND** the declared workflow does not imply a hidden or missing runner stage

### Requirement: REQ-WD-002 Integrate uses the standard task/check stage contract

The Integrate stage SHALL execute deterministic integration work as standard WorkflowRun tasks and SHALL run final verification as a read-only WorkflowRun check. Integrate ordering, task failure handling, delivery metadata (rebase and push), freeze behavior, and post-publish health failure handling SHALL be decided by StageRun rather than by runner-local step state.

#### Scenario: Integrate runs tasks before checks

- **WHEN** an issue enters Integrate
- **THEN** the stage SHALL execute `integrate:spec-sync`, `integrate:archive-change`, `integrate:rebase`, and `integrate:push` as ordered StageRun tasks
- **AND** it SHALL run `health:integrate` only after those tasks succeed

#### Scenario: Integrate failure stays local

- **WHEN** `integrate:spec-sync`, `integrate:archive-change`, `integrate:rebase`, or `integrate:push` fails
- **THEN** later Integrate tasks and checks SHALL NOT run
- **AND** the issue SHALL remain associated with Integrate failure evidence

#### Scenario: Post-publish health cannot auto-fix

- **WHEN** `health:integrate` fails after `integrate:push` has completed
- **THEN** the failure SHALL be recorded as a post-publish delivery failure
- **AND** the stage SHALL NOT apply any check failure policy that would modify code after the push freeze point

#### Scenario: Push is the single push owner

- **WHEN** the default Integrate workflow is loaded
- **THEN** `integrate:push` SHALL be the only default task that pushes delivery changes to the remote
- **AND** the workflow SHALL NOT declare a separate delivery push task outside `integrate:push`

### Requirement: Stage definitions declare workflow behavior policies

The built-in workflow definition SHALL describe each runnable stage with declarative stage configuration that includes its default tasks, work sources, task execution policy, check policy, approval policy, repair policy, and invalidation policy. The stage definition SHALL describe the shape and policies of the stage and SHALL NOT execute tasks, run checks, mutate artifacts, mutate git state, or decide stage transitions directly.

#### Scenario: Default stages expose declarative policies

- **WHEN** the system loads the built-in workflow definition
- **THEN** the Plan, Build, Check, and Integrate stage definitions SHALL each expose the work sources, task execution policy, check policy, approval policy when applicable, repair policy when applicable, and invalidation policy needed to execute that stage
- **AND** the declared stage order SHALL match `mohist-default.workflow.yaml`

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

The declarative definitions for Plan, Build, Check, and Integrate SHALL preserve the existing user-visible workflow semantics while moving stage differences into configuration and registries. The Integrate definition SHALL preserve a single push owner for default delivery.

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
- **THEN** it SHALL execute spec sync, change archive, branch rebase, and branch push as ordered stage tasks
- **AND** it SHALL run the Integrate health check only after those tasks succeed

### Requirement: StageDefinition separates static promises from run-owned work sources

StageDefinition SHALL describe static stage tasks/checks, dynamic work sources, and execution or invalidation policies without storing run-specific dynamic task identities.

#### Scenario: Static tasks remain definition promises
- **WHEN** a stage has default required work such as Plan or Integrate tasks
- **THEN** StageDefinition SHALL declare those static tasks and checks as the stage promise
- **AND** WorkflowRun SHALL require matching StageRun evidence before completion

#### Scenario: Build tasks are not copied into static definitions
- **WHEN** Build reads generated tasks from `tasks.json`
- **THEN** StageDefinition MAY describe the dynamic work source and execution policy
- **AND** generated task ids from `tasks.json` SHALL live only as StageRun TaskRun records for that run

#### Scenario: Runtime task kinds are policy not static promises
- **WHEN** runtime work such as `rebase-branch`, repair, retry, or convergence is appended because of this run's facts
- **THEN** StageDefinition MAY define execution or invalidation policy for that kind of work
- **AND** it SHALL NOT list the specific runtime occurrence as a static required task

### Requirement: Stage task policies can reference named agent sessions

Stage task execution policy SHALL allow an agent-session task to declare an optional `agentSessionRef` that names the logical agent session used by that task within the current stage attempt. The field SHALL be interpreted only by agent-session execution and SHALL NOT imply previous-task reuse or a session group.

#### Scenario: Agent-session policy carries a named ref
- **WHEN** a stage definition declares an agent-session task with `agentSessionRef: "plan-artifacts"`
- **THEN** dispatch SHALL pass that reference to the agent-session task input
- **AND** task identity, ordering, status, attempts, outputs, and artifact validation SHALL remain separate from the session reference

#### Scenario: Omitted ref keeps task-local behavior
- **WHEN** an agent-session task policy omits `agentSessionRef`
- **THEN** dispatch SHALL build the same task-local agent-session input used before this change
- **AND** Build and Check tasks SHALL remain task-local unless their policies explicitly set a reference

### Requirement: Default Plan artifact tasks share one planning session reference

The built-in Plan stage definition SHALL configure `proposal`, `specs`, `design`, `tasks`, and `self-review` agent-session tasks with the same `agentSessionRef`, `plan-artifacts`, while keeping repair and rebase operational tasks separate unless explicitly configured otherwise.

#### Scenario: Default Plan policies use plan-artifacts
- **WHEN** the built-in workflow definition is loaded
- **THEN** the default Plan artifact task policies for `proposal`, `specs`, `design`, `tasks`, and `self-review` SHALL declare `agentSessionRef: "plan-artifacts"`
- **AND** each artifact task SHALL still appear as an independent Plan task row

#### Scenario: Stage can define multiple named refs
- **WHEN** a stage definition assigns different agent-session tasks to two or more distinct `agentSessionRef` values
- **THEN** tasks with the same ref SHALL share one real session for the stage attempt
- **AND** tasks with different refs SHALL use different real sessions

### Requirement: REQ-WD-001 workflow tasks, checks, and reactions declare structured contracts

Workflow definitions SHALL support generic result contracts, self-repair policy, invalidation hints, and reaction input selectors for task/check/reaction orchestration.

#### Scenario: Task definition declares a structured result contract

- **WHEN** a task produces judgeable AI output
- **THEN** its definition MAY declare a `resultContract` with contract kind, required marker policy, allowed markers, item policy, and declared output source
- **AND** built-in judgment tasks SHALL default to a promise-marker contract when PASS/FAIL is judgeable

#### Scenario: Task definition declares self-repair boundaries

- **WHEN** a task implementation may repair during execution
- **THEN** its definition SHALL express `selfRepairPolicy` boundaries, allowed scopes, max attempts, verification requirements, and disallowed repair reasons
- **AND** checks SHALL NOT use this policy to modify files or start agents

#### Scenario: Reaction definition selects failed context

- **WHEN** a check failure schedules a reaction task
- **THEN** the reaction definition SHALL be able to select failed check output, selected task outputs, artifacts, structured item batches, snapshot metadata, and retry/recheck policy as explicit inputs

### Requirement: Workflow YAML supports approval feedback task configuration

The workflow profile YAML SHALL support an `approval.feedback` section that defines what task to execute when a user requests changes at an approval gate. The configuration SHALL be minimal and SHALL only describe the feedback task identity, not a full feedback schema.

#### Scenario: Default feedback task configuration

- **WHEN** the workflow YAML defines:
  ```yaml
  approval:
    feedback:
      task:
        id: apply-feedback
        title: Apply approval feedback
        uses: mohist/acp-agent
        with:
          session: ${{ stage.name }}
          prompt: ${{ prompts.apply-feedback }}
  ```
- **THEN** the workflow engine SHALL schedule this task when feedback is created
- **AND** the task SHALL use the configured session name and prompt

#### Scenario: Feedback task uses shared task execution primitives

- **WHEN** `approval.feedback.task` is configured
- **THEN** the task SHALL resolve through the standard task loader and handler registries
- **AND** the task execution policy SHALL follow the same contract as other agent-session tasks

#### Scenario: No custom feedback task falls back to built-in default

- **WHEN** the workflow YAML has no `approval.feedback` section
- **THEN** the system SHALL use the built-in `apply-feedback` task with the built-in `apply-feedback.prompt`

### Requirement: Feedback task configuration does not define feedback schema

The workflow YAML `approval.feedback` section SHALL NOT define a feedback schema, data shape, validation rules, or runtime state fields. Feedback is runtime state, not workflow definition data.

#### Scenario: YAML contains only task identity

- **WHEN** the `approval.feedback.task` configuration is inspected
- **THEN** it SHALL contain task id, title, uses, and with configuration
- **AND** it SHALL NOT contain feedback field definitions, validation rules, category enums, severity levels, or data shapes

### Requirement: Prompt reference in feedback task uses standard template variables

The feedback task prompt reference SHALL support the standard workflow template variables including `${{ prompts.apply-feedback }}`, `${{ stage.name }}`, `${{ issue.number }}`, and `${{ project.id }}`.

#### Scenario: Prompt variable substitution

- **WHEN** the feedback task is dispatched
- **THEN** `${{ prompts.apply-feedback }}` SHALL resolve to the built-in or custom apply-feedback prompt content
- **AND** `${{ stage.name }}` SHALL resolve to the current stage name
- **AND** `${{ issue.number }}` SHALL resolve to the current issue number
- **AND** `${{ project.id }}` SHALL resolve to the current project id

### Requirement: Feedback is gateway-scoped, not stage-scoped in YAML

The `approval.feedback` section SHALL be at the workflow root level (shared by all stages with approval gates), not duplicated per-stage. Stage-specific feedback task overrides SHALL NOT be supported initially.

#### Scenario: Single feedback configuration for all stages

- **WHEN** the workflow YAML has one `approval.feedback` section
- **AND** multiple stages have approval gates (Plan, Check)
- **THEN** the same feedback task configuration SHALL apply when feedback is created at any approval gate
- **AND** the `stage.name` template variable SHALL reflect the actual stage where feedback was requested

#### Scenario: Per-stage feedback overrides are not supported

- **WHEN** the workflow YAML is loaded
- **THEN** the system SHALL NOT look for per-stage `approval.feedback` configuration inside individual stage definitions

### Requirement: Built-in mohist/pr definition diverges from mohist/default only in the delivery task

The system SHALL provide a built-in `mohist/pr` workflow definition whose Plan, Build, Check, and Integrate stages — including tasks, approval gates, repair policy, check policy, and invalidation policy — match `mohist/default` exactly. The `mohist/pr` Integrate stage SHALL preserve the same ordered task list (`integrate:spec-sync` → `integrate:archive-change` → `integrate:prepare` → `integrate:publish`) and the same single-push-owner invariant. The ONLY difference SHALL be that the `integrate:publish` task uses the `mohist/publish-via-pr` action instead of `mohist/publish`.

#### Scenario: mohist/pr shares plan/build/check with mohist/default

- **WHEN** the `mohist/pr` workflow definition is loaded
- **THEN** the Plan, Build, and Check stage definitions SHALL match `mohist/default` task-for-task
- **AND** the approval gates, repair policies, check policies, and invalidation policies SHALL match `mohist/default`

#### Scenario: mohist/pr Integrate differs only in the publish action

- **WHEN** the `mohist/pr` Integrate stage is loaded
- **THEN** the ordered tasks SHALL be `integrate:spec-sync`, `integrate:archive-change`, `integrate:prepare`, and `integrate:publish`
- **AND** the `integrate:publish` task SHALL use the `mohist/publish-via-pr` action
- **AND** every other Integrate task SHALL match `mohist/default`

#### Scenario: mohist/pr preserves a single push owner

- **WHEN** the `mohist/pr` Integrate workflow is loaded
- **THEN** `integrate:publish` SHALL be the only task that pushes delivery changes to the remote
- **AND** the workflow SHALL NOT declare a separate `integrate:push` task or any other remote-writing task
