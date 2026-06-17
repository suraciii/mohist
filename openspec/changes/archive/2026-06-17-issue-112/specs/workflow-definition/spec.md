## MODIFIED Requirements

### Requirement: REQ-WD-001 Integrate owns intelligent OpenSpec spec sync

The workflow SHALL treat `integrate:spec-sync` as the stage task that writes approved change delta specs into main OpenSpec specs. The task SHALL read the change delta specs and existing main specs, resolve clear ADDED, MODIFIED, REMOVED, and RENAMED intent, and preserve separate integration steps for spec sync, archive, merge, and final health. The task SHALL commit generated spec changes to the worktree or report a no-change result before completing; the runner SHALL verify `git status --porcelain` is clean before marking the task completed.

#### Scenario: Integrate runs distinct ordered steps

- **WHEN** an approved change enters INTEGRATE
- **THEN** the workflow SHALL run `integrate:spec-sync` before `integrate:archive-change`
- **AND** it SHALL keep `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, and `final-health` as distinct task or step history entries

#### Scenario: Archive waits for spec sync

- **WHEN** `integrate:spec-sync` fails
- **THEN** the workflow SHALL NOT archive the OpenSpec change
- **AND** it SHALL NOT merge the candidate or run final health

#### Scenario: Spec-sync commits or reports no-change before completing

- **WHEN** `integrate:spec-sync` generates or copies spec changes into the worktree
- **THEN** the task SHALL commit the changes or report that no changes were made
- **AND** the runner SHALL verify `git status --porcelain` is clean before marking the task completed

### Requirement: REQ-WD-002 Integrate uses the standard task/check stage contract

The Integrate stage SHALL execute deterministic integration work as standard WorkflowRun tasks and SHALL run final verification as a read-only WorkflowRun check. The `integrate:merge` task SHALL include `push: true` and `remote: origin` in its configuration so that push is part of the merge completion contract rather than a separate user-facing task. Integrate ordering, task failure handling, merge delivery metadata, freeze behavior, and post-merge health failure handling SHALL be decided by StageRun rather than by runner-local step state.

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

#### Scenario: Push is part of merge, not a separate task

- **WHEN** the default Integrate workflow is loaded
- **THEN** `integrate:merge` SHALL include `push: true` and `remote: origin` in its `with` block
- **AND** the workflow SHALL NOT declare a separate `integrate:push` task
