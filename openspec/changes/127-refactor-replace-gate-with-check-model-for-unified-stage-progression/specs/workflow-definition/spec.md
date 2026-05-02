## MODIFIED Requirements

### Requirement: OpenSpec workflow structure

The system SHALL support a 4-stage workflow for OpenSpec-style changes, where each stage uses the unified Task + Check + Reaction model.

**Stages:**
1. **plan** - Generate Change artifacts + self-review + user-approval check
2. **build** - Execute tasks from tasks.json + code-compiles check (no user-approval)
3. **check** - Run build-test + AI review + user-approval check
4. **done** - Rebase, verify, merge, cleanup (no user-approval)

Stage advancement is determined solely by all checks passing (see unified-check-model spec).

#### Scenario: Default OpenSpec workflow

- **WHEN** an issue starts with `mo propose` or `mo issue start`
- **AND** the system detects no existing Change (or creates new version)
- **THEN** it follows the 4-stage workflow
- **AND** each stage uses BaseStageRunner with its declared Task list and Check list
- **AND** stages advance only when all their checks pass

### Requirement: Plan stage behavior

The plan stage SHALL generate Change artifacts, perform self-review, and include a `user-approval` check in its checks list.

#### Scenario: Generate Change artifacts

- **WHEN** plan stage executes via BaseStageRunner
- **THEN** the stage's Task list executes:
  1. Generate Proposal
  2. Generate Specs
  3. Generate Design
  4. Generate Tasks
  5. Self-Review
- **AND** after tasks complete, the stage's Check list executes:
  1. proposal-complete
  2. specs-complete
  3. design-complete
  4. tasks-valid
  5. self-review-passed
  6. user-approval

#### Scenario: Self-review iteration

- **WHEN** self-review check fails
- **THEN** the check's reaction (`retry-task`) triggers re-execution of the plan task list
- **AND** retry count increments
- **AND** if max retries (3) exceeded, the reaction escalates

#### Scenario: Plan awaiting user approval

- **WHEN** all artifact checks pass but `user-approval` check has not been satisfied
- **THEN** the `user-approval` check triggers `ask-user` reaction
- **AND** pipeline pauses
- **AND** system emits `approval_requested` event

### Requirement: Review stage behavior

**REMOVED** — The standalone review stage is eliminated. User review is now handled by the `user-approval` check within the Plan and Check stages.

### Requirement: Build stage behavior

The build stage SHALL execute tasks from tasks.json and verify code compiles, with no user-approval check.

#### Scenario: Build task execution via BaseStageRunner

- **WHEN** build stage executes via BaseStageRunner
- **THEN** the stage's Task list executes (tasks from tasks.json in DAG order)
- **AND** after tasks complete, the stage's Check list executes:
  1. all-tasks-complete
  2. code-compiles
- **AND** if all checks pass, stage automatically advances to Check (no user-approval pause)

#### Scenario: Build task failure triggers retry

- **WHEN** a task fails during execution
- **THEN** the `all-tasks-complete` check fails
- **AND** its reaction (`retry-task`) re-executes the failed task with failure context

#### Scenario: Build code-compiles failure triggers auto-fix

- **WHEN** `code-compiles` check fails
- **THEN** its reaction (`auto-fix`) triggers an AI fix attempt
- **AND** if fix succeeds, the check re-runs and passes
- **AND** if fix fails after max attempts, the reaction escalates to Plan

### Requirement: Check stage behavior

The check stage SHALL run build-test and AI review, and include a `user-approval` check.

#### Scenario: Check execution via BaseStageRunner

- **WHEN** check stage executes via BaseStageRunner
- **THEN** the stage's Task list executes:
  1. Run Build-Test
  2. Run AI Review
- **AND** after tasks complete, the stage's Check list executes:
  1. build-test-passed
  2. ai-review-passed
  3. user-approval

#### Scenario: Build-test failure with auto-fix

- **WHEN** `build-test-passed` check fails
- **THEN** its reaction (`auto-fix`, max 2 attempts) triggers AI fix
- **AND** if still fails after max attempts, escalates to Build stage

#### Scenario: AI review failure escalates to Plan

- **WHEN** `ai-review-passed` check fails
- **THEN** its reaction (`escalate`) transitions issue to Plan stage
- **AND** the failure context is passed to Plan

#### Scenario: Archive Change

- **WHEN** check stage all checks pass (including user-approval)
- **THEN** system archives Change to `openspec/changes/archive/YYYY-MM-DD-{name}/`
- **AND** advances to Done stage

#### Scenario: User rejection escalates to Plan

- **WHEN** user rejects during `user-approval` check
- **THEN** the check's fallback reaction (`escalate` to Plan) triggers
- **AND** issue transitions to Plan stage with user's feedback as context

### Requirement: Backward compatibility

The system SHALL support traditional workflow for issues without Change artifacts, using the same unified BaseStageRunner execution loop.

#### Scenario: Traditional workflow

- **WHEN** an issue has no `openspec/changes/` directory
- **THEN** it follows the 4-stage pipeline using BaseStageRunner
- **AND** stages still use Task + Check + Reaction model with simplified task/check lists
- **AND** no Change artifacts are created
