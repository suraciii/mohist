# OpenSpec Capability: workflow-definition

### Requirement: OpenSpec workflow structure

The system SHALL support a 4-stage workflow for OpenSpec-style changes.

**Stages:**
1. **plan** - Generate Change artifacts + self-review
2. **review** - Human review (approval gate)
3. **build** - Ralph-style task execution
4. **check** - Automated testing + human acceptance + archival (approval gate)

#### Scenario: Default OpenSpec workflow
- **WHEN** an issue starts with `mo propose` or `mo issue start`
- **AND** the system detects no existing Change (or creates new version)
- **THEN** it follows the 4-stage workflow
- **AND** each stage has specific responsibilities

### Requirement: Plan stage behavior

The plan stage SHALL generate Change artifacts and perform self-review.

#### Scenario: Generate Change artifacts
- **WHEN** plan stage executes
- **THEN** the agent:
  1. Explores codebase
  2. Creates `openspec/changes/{name}/`
  3. Writes proposal.md, design.md, specs/*.md
  4. Performs self-review (max 3 iterations)
  5. Generates tasks.json if review passes
  6. Or pauses if max iterations reached

#### Scenario: Self-review iteration
- **WHEN** self-review iteration starts
- **THEN** agent validates:
  - All specs have clear AC
  - Design covers edge cases
  - Requirements are complete
- **AND** if issues found, agent fixes them
- **AND** if no improvement after 3 iterations, stage fails

### Requirement: Review stage behavior

The review stage SHALL be an approval gate for human review.

#### Scenario: Human review Change
- **WHEN** review stage executes
- **THEN** the system presents Change artifacts to user
- **AND** user can:
  - Edit any file (proposal.md, design.md, specs/*.md, tasks.json)
  - Add comments to issue
  - Approve to proceed to build
  - Or go back to plan

### Requirement: Build stage behavior

The build stage SHALL execute Ralph-style task loop.

#### Scenario: Ralph loop execution
- **WHEN** build stage executes
- **THEN** main-agent:
  1. Reads tasks.json
  2. For each pending task:
     - Assembles context (proposal + design + spec + learnings)
     - Calls spawn_coder
     - Verifies AC
     - Stores learning
     - Updates passes/attempts/error in tasks.json
  3. Continues until all tasks complete or failure

### Requirement: Check stage behavior

The check stage SHALL perform automated testing and human acceptance.

#### Scenario: Automated testing
- **WHEN** check stage starts
- **THEN** agent automatically runs:
  - `npm test` (or equivalent)
  - `npm run lint` (or equivalent)
  - Any other validation commands from workflow config
- **AND** reports results in issue comment

#### Scenario: Human acceptance
- **WHEN** automated tests pass
- **THEN** system waits for human approval (approval gate)
- **AND** user can:
  - Review all changes
  - Approve to complete
  - Or request fixes (loop back to build)

#### Scenario: Archive Change
- **WHEN** check stage completes with approval
- **THEN** system moves Change to `openspec/changes/archive/YYYY-MM-DD-{name}/`
- **AND** marks issue as done

### Requirement: Backward compatibility

The system SHALL support traditional workflow for issues without Change artifacts.

#### Scenario: Traditional workflow
- **WHEN** an issue has no `openspec/changes/` directory
- **THEN** it follows the traditional 3-stage workflow:
  - plan (temporary output)
  - build (single spawn_coder)
  - check (validation)
- **AND** no Change artifacts are created

### Requirement: REQ-WD-001 Integrate owns intelligent OpenSpec spec sync

The workflow SHALL treat `integrate:spec-sync` as the stage task that writes approved change delta specs into main OpenSpec specs. The task SHALL read the change delta specs and existing main specs, resolve clear ADDED, MODIFIED, REMOVED, and RENAMED intent, and preserve separate integration steps for spec sync, archive, merge, and final health.

#### Scenario: Integrate runs distinct ordered steps
- **WHEN** an approved change enters INTEGRATE
- **THEN** the workflow SHALL run `integrate:spec-sync` before `integrate:archive-change`
- **AND** it SHALL keep `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, and `final-health` as distinct task or step history entries

#### Scenario: Archive waits for spec sync
- **WHEN** `integrate:spec-sync` fails
- **THEN** the workflow SHALL NOT archive the OpenSpec change
- **AND** it SHALL NOT merge the candidate or run final health

### Requirement: default runnable workflow stages match executed pipeline (REQ-001)

The built-in default workflow definition SHALL only declare stages that the runner can actually execute.

#### Scenario: Explore is not declared as a default runnable stage
- **WHEN** the system loads the built-in default workflow
- **THEN** the declared runnable stages do not include `explore`

#### Scenario: Default runnable workflow matches execution order
- **WHEN** no project-specific workflow overrides are present
- **THEN** the default runnable stage list is `plan -> build -> check -> integrate -> done`
- **AND** the declared workflow does not imply a hidden or missing runner stage

### Requirement: REQ-WD-002 Integrate uses the standard task/check stage contract

The Integrate stage SHALL execute deterministic integration work as standard WorkflowRun tasks and SHALL run final verification as a read-only WorkflowRun check. Integrate ordering, task failure handling, merge delivery metadata, freeze behavior, and post-merge health failure handling SHALL be decided by StageRun rather than by runner-local step state.

#### Scenario: Integrate runs tasks before checks

- **WHEN** an issue enters Integrate
- **THEN** the stage SHALL execute `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` as ordered StageRun tasks
- **AND** it SHALL run `health:integrate` only after those tasks succeed

#### Scenario: Integrate failure stays local

- **WHEN** `integrate:spec-sync`, `integrate:archive-change`, or `integrate:merge` fails
- **THEN** later Integrate tasks and checks SHALL NOT run
- **AND** the issue SHALL remain associated with Integrate failure evidence

#### Scenario: Post-merge health cannot auto-fix

- **WHEN** `health:integrate` fails after merge has completed
- **THEN** the failure SHALL be recorded as a post-merge delivery failure
- **AND** the stage SHALL NOT apply any check failure policy that would modify code after the merge freeze point

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

