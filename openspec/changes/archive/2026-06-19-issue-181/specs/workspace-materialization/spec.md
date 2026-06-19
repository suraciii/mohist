## ADDED Requirements

### Requirement: A WorkflowRun owns exactly one execution workspace

A WorkflowRun SHALL own exactly one execution workspace for the lifetime of the run. The workspace SHALL be the single working tree against which plan, build, check, and integrate work executes. A workflow run SHALL NOT create a second, per-stage, or per-task checkout of the workflow workspace; stage-specific branch context that cannot be satisfied on the run branch SHALL be handled in isolated temporary workspaces outside the workflow workspace, not by materializing a new workflow workspace.

#### Scenario: One workspace serves every stage of a run

- **WHEN** a WorkflowRun executes plan, build, check, and integrate work
- **THEN** all of that work SHALL execute against the one execution workspace owned by the run
- **AND** the run SHALL NOT materialize a new workflow workspace for any later stage or task

#### Scenario: A run does not own more than one execution workspace

- **WHEN** work-item dispatch is asked to prepare the workflow workspace for a run that already owns a bound workspace
- **THEN** the runner SHALL use the already-bound workspace
- **AND** the runner SHALL NOT create a second workflow workspace for the same run

### Requirement: Workflow start materializes and binds the workflow workspace before the first task dispatch

Workflow start SHALL perform the first and only repository materialization for the run before the first task is dispatched. Materialization SHALL prepare or refresh the runner-owned bare repository cache from `repository.gitUrl`, create the workflow workspace at the run's bound `workspace.path`, check out the configured repository base branch, create and check out the workflow run branch, and write the workspace marker. The run SHALL bind the resulting workspace identity (path, branch, run ownership) to the WorkflowRun before the first StageRun task is scheduled.

#### Scenario: Start performs the first and only materialization

- **WHEN** a WorkflowRun starts
- **THEN** workflow start SHALL prepare or refresh the runner-owned bare repository cache from `repository.gitUrl`
- **AND** it SHALL create the workflow workspace at the bound `workspace.path`
- **AND** it SHALL check out the configured repository base branch
- **AND** it SHALL create and check out the workflow run branch
- **AND** it SHALL write the workspace marker
- **AND** the workflow SHALL NOT dispatch the first task until this materialization has completed

#### Scenario: Workspace identity is bound before the first task dispatch

- **WHEN** workflow start finishes materializing the workspace
- **THEN** the WorkflowRun SHALL record the bound workspace identity for the run
- **AND** the bound identity SHALL carry the workspace path, the run branch, and the owning workflow run id
- **AND** the first StageRun task SHALL NOT be scheduled until the workspace identity is bound

#### Scenario: Materialization failure stops the run before any task runs

- **WHEN** workflow-start materialization fails
- **THEN** the WorkflowRun SHALL NOT dispatch the first task
- **AND** the failure SHALL be surfaced as a workflow-start workspace-materialization failure
- **AND** the failure SHALL NOT be reported as an ordinary business-task failure

### Requirement: Work-item dispatch verifies the bound workspace without re-materializing it

Work-item dispatch SHALL consume the already-bound workflow workspace and SHALL NOT clone the remote repository, recreate the workflow workspace, or re-run workflow-start materialization. Dispatch-time workspace handling MAY verify that the workspace exists, belongs to the same workflow run, is on its `workspace.branch`, and satisfies task boundary invariants; a verification failure SHALL be reported as an explicit workspace or branch-invariant failure rather than recovered by re-cloning.

#### Scenario: Dispatch does not re-clone the workflow repository

- **WHEN** a task or check is dispatched for a run that owns a bound workflow workspace
- **THEN** the runner SHALL NOT run `git clone` against the remote repository to prepare the workflow workspace
- **AND** the runner SHALL NOT re-run workflow-start materialization steps

#### Scenario: Dispatch verifies the workspace belongs to the same run

- **WHEN** work-item dispatch resolves the workflow workspace for a run
- **THEN** it SHALL verify the workspace exists at the bound path
- **AND** it SHALL verify the workspace identity belongs to the same workflow run id
- **AND** a mismatch SHALL be reported as a workspace identity failure rather than recovered by re-materialization

#### Scenario: Dispatch verifies the workspace is on the run branch

- **WHEN** work-item dispatch begins a task against the workflow workspace
- **THEN** it SHALL verify the workspace is on `workspace.branch` at the task boundary
- **AND** a branch-invariant mismatch SHALL be reported as a runner/action branch-invariant violation
- **AND** the mismatch SHALL NOT be recovered by re-cloning the repository

#### Scenario: Agent-job standalone workspaces are exempt from the materialize/verify contract

- **WHEN** work-item dispatch resolves a work item whose owner-kind is `agent-job`
- **THEN** dispatch SHALL resolve the workspace from the supplied `workspace.path` without invoking workflow-workspace materialization or verification
- **AND** it SHALL NOT clone a repository or write a workflow workspace marker for an agent-job work item

### Requirement: Workspace missing or corrupt is a workflow infrastructure failure

A workflow workspace that is missing, corrupt, or identity-mismatched at dispatch time SHALL be treated as a workflow infrastructure failure, not as a business-task clone failure. The failure SHALL be attributed to the workflow/runner infrastructure and SHALL be distinct from dirty-worktree, conflict, base-moved, and provider failures. Ordinary task work SHALL NOT be held responsible for a failure to materialize infrastructure it was never asked to materialize.

#### Scenario: Missing workspace is an infrastructure failure

- **WHEN** work-item dispatch finds the bound workflow workspace path does not exist
- **THEN** the failure SHALL be reported with a workspace-missing infrastructure kind
- **AND** the failure SHALL be attributed to workflow infrastructure, not to issue work
- **AND** the failure SHALL NOT be reported as a task clone failure

#### Scenario: Corrupt or mismatched workspace is an infrastructure failure

- **WHEN** work-item dispatch finds the workspace marker missing, unreadable, or bound to a different workflow run
- **THEN** the failure SHALL be reported with a workspace-corrupt or workspace-identity-mismatch infrastructure kind
- **AND** it SHALL be distinct from dirty-worktree, conflict, base-moved, and provider failures

#### Scenario: Startup materialization failure is distinguished from task failure

- **WHEN** workflow-start workspace materialization fails or a dispatch-time workspace verification fails
- **THEN** CLI, API, and UI failure surfaces SHALL present the failure as a workflow workspace-materialization failure
- **AND** the failure category SHALL be distinguishable from ordinary task execution failure

### Requirement: mohist/prepare operates inside the bound workflow workspace

`mohist/prepare` SHALL run its fetch and rebase against the existing bound workflow workspace on its `workspace.branch`. It SHALL NOT trigger workflow workspace creation, repository cloning, or workflow-start materialization. A run that has not yet bound a workflow workspace SHALL fail prepare as a workspace-missing infrastructure failure rather than materialize a workspace on demand.

#### Scenario: Prepare uses the bound workspace without materializing

- **WHEN** `integrate:prepare` runs for a run that owns a bound workflow workspace
- **THEN** `mohist/prepare` SHALL fetch the remote base ref and rebase inside the bound workflow workspace
- **AND** it SHALL NOT run `git clone` to create or recover the workflow workspace
- **AND** it SHALL leave the workflow workspace on its `workspace.branch`

#### Scenario: Prepare without a bound workspace fails as infrastructure

- **WHEN** `mohist/prepare` runs for a run whose workflow workspace is missing or unbound
- **THEN** prepare SHALL fail with a workspace-missing infrastructure failure
- **AND** it SHALL NOT materialize a workflow workspace as a side effect of prepare

### Requirement: Hardened runner-owned repository cache handling

The runner-owned bare repository cache SHALL be preserved across transient `git fetch` failures. A failed `git fetch origin` SHALL NOT delete an existing cache. Cache replacement SHALL be limited to a clear remote-identity mismatch or verified cache corruption, and SHALL NOT delete an object store that is still referenced by an active workflow workspace. Repository cache paths SHALL remain runner implementation details and SHALL NOT be exposed as project configuration, repository configuration, workflow variables, or public API fields.

#### Scenario: A failed fetch does not delete the existing cache

- **WHEN** the bare repository cache already exists for a `repository.gitUrl`
- **AND** `git fetch origin` fails transiently
- **THEN** the runner SHALL NOT delete the existing cache directory
- **AND** workflow workspaces whose shared object store references that cache SHALL remain valid

#### Scenario: Cache replacement only on identity mismatch or verified corruption

- **WHEN** the runner considers replacing the existing bare cache
- **THEN** replacement SHALL be allowed only when the cache origin identity does not match `repository.gitUrl` or the cache is verified to be corrupt
- **AND** a transient fetch failure or network error SHALL NOT by itself justify cache replacement

#### Scenario: Referenced object stores are not deleted

- **WHEN** cache replacement or cleanup would delete a bare cache object store
- **AND** an active workflow workspace references that object store through shared alternates
- **THEN** the runner SHALL NOT delete the object store
- **AND** the runner SHALL keep the referenced object store intact until active workspace references are released

#### Scenario: Cache paths are not exposed

- **WHEN** workflow variables, project configuration, repository configuration, or a public API response is composed
- **THEN** the repository cache path SHALL NOT appear as a configurable or observable field
- **AND** the cache path SHALL remain a runner-owned implementation detail
