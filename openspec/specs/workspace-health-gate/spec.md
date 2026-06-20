# OpenSpec Capability: workspace-health-gate

### Requirement: Workspace consumption detects and heals residual rebase/merge state at entry

The runner SHALL run a workspace health gate at the entry of every workspace consumption path (`verify` and `materialize`) before any task, check, or integrate action is dispatched against the workflow workspace. The health gate SHALL detect residual in-progress git operations by probing for `rebase-merge`, `rebase-apply`, `MERGE_HEAD`, and `CHERRY_PICK_HEAD`. When any residual state is detected, the health gate SHALL abort the in-progress operation (`git rebase --abort`, `git merge --abort`, or `git cherry-pick --abort` as appropriate) before the workspace is handed to any task. A workspace with no residual state SHALL pass through the health gate unchanged. The health gate SHALL be the only recovery mechanism relied upon once disposable landing workspaces are removed, and it SHALL make a single workflow workspace safe to keep across runner crashes.

#### Scenario: Residual rebase state is aborted at entry

- **GIVEN** the workflow workspace has a residual `rebase-merge` or `rebase-apply` directory left by a crashed prior run
- **WHEN** the runner consumes the workspace through `verify` or `materialize`
- **THEN** the health gate SHALL detect the residual rebase state before any task runs
- **AND** it SHALL run `git rebase --abort` before the workspace is handed to a task
- **AND** the workspace SHALL NOT be handed to any task while residual rebase state remains

#### Scenario: Residual merge or cherry-pick state is aborted at entry

- **GIVEN** the workflow workspace has a residual `MERGE_HEAD` or `CHERRY_PICK_HEAD`
- **WHEN** the runner consumes the workspace through `verify` or `materialize`
- **THEN** the health gate SHALL detect the residual merge or cherry-pick state before any task runs
- **AND** it SHALL abort the in-progress operation before the workspace is handed to a task

#### Scenario: Clean workspace passes through unchanged

- **GIVEN** the workflow workspace has no residual rebase, merge, or cherry-pick state
- **WHEN** the runner consumes the workspace through `verify` or `materialize`
- **THEN** the health gate SHALL take no recovery action
- **AND** the workspace SHALL be handed to the task unchanged

### Requirement: Health gate recovery restores the run branch as the working context

After aborting a residual in-progress operation, the health gate SHALL recover the workflow workspace to the run branch context. The workspace SHALL be checked out to `workspace.branch`, the working tree and index SHALL be aligned to the `workspace.branch` ref, and no conflict markers or partial resolution state from the aborted operation SHALL remain. The recovered workspace SHALL be on `workspace.branch` and clean of the residual operation's artifacts.

#### Scenario: Recovery lands on the run branch aligned to its ref

- **WHEN** the health gate has aborted a residual rebase, merge, or cherry-pick operation
- **THEN** the workspace SHALL be checked out to `workspace.branch`
- **AND** the working tree and index SHALL be aligned to the `workspace.branch` ref
- **AND** no conflict markers or partial resolution state SHALL remain

### Requirement: Health gate recovery is non-destructive to committed run-branch work

The health gate's recovery SHALL NOT discard, rewrite, or move commits already reachable at the `workspace.branch` ref. Because a rebase advances the run branch ref only on success, commits an agent pushed to the run branch before a mid-rebase crash SHALL remain reachable at the `workspace.branch` ref after recovery. The health gate SHALL be the crash self-healing mechanism that makes a single workspace safe to keep without a disposable landing clone.

#### Scenario: Committed work survives a mid-rebase crash

- **GIVEN** an agent committed work to `workspace.branch` and a later rebase crashed mid-flight leaving residual `rebase-merge` state
- **WHEN** the health gate runs at the next workspace consumption entry
- **THEN** the recovery SHALL NOT move or discard the `workspace.branch` ref
- **AND** the commits that were on `workspace.branch` before the rebase SHALL remain reachable at `workspace.branch`

#### Scenario: A simulated crashed workspace self-heals

- **GIVEN** a workspace fixture left in a mid-rebase state with residual `rebase-merge` and conflict markers
- **WHEN** the runner consumes the workspace through `verify` or `materialize`
- **THEN** the health gate SHALL recover the workspace to `workspace.branch` aligned to its ref
- **AND** a subsequent task SHALL be dispatchable without manual `git checkout` or `rebase --abort` intervention