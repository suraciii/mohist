### Requirement: Workflow start resolves the Issue target repository

At the start of each workflow run, Mohist SHALL resolve the Issue's stored target name against the repositories currently declared by its Project and SHALL provide that repository's canonical name, Git URL, and base branch as the authoritative execution context. Configurable workflow variables MUST NOT redirect repository operations to another repository. If the target declaration cannot be resolved, workflow start SHALL fail before a workspace is created and MUST NOT fall back to the Project default.

#### Scenario: Start on an explicit non-default target

- **WHEN** an Issue bound to `web` starts in a Project whose default repository is `server`
- **THEN** the workflow SHALL use the canonical name, Git URL, and base branch declared for `web`

#### Scenario: A default change does not redirect startup

- **WHEN** an Issue was bound to `server` and the Project later makes `web` the default
- **THEN** starting that Issue SHALL still resolve and use repository `server`

#### Scenario: Workflow variables cannot override the binding

- **WHEN** configurable workflow variables contain repository values different from the Issue target
- **THEN** every repository operation SHALL use the context resolved from the stored Issue target

#### Scenario: Missing target declaration fails closed

- **WHEN** workflow start is requested for an Issue whose stored target is no longer declared
- **THEN** startup SHALL fail with a repository error, create no workspace, and MUST NOT substitute the Project default

### Requirement: A workflow run uses one coherent repository context

Every workflow run SHALL use one coherent target repository context for workspace creation, review, maintenance, recovery, and delivery. Repository metadata changed after a run starts MUST NOT partially redirect that run or mix new metadata with its existing workspace. A later workflow run SHALL resolve the then-current metadata for the Issue's unchanged target name.

#### Scenario: Metadata changes before workflow start

- **WHEN** the Project changes the Git URL or base branch for target `web` before an Issue workflow starts
- **THEN** the new workflow run SHALL use the updated `web` metadata while the Issue binding remains `web`

#### Scenario: Metadata changes during a workflow run

- **WHEN** the Project changes the declaration for `web` after a workflow run has created its `web` workspace
- **THEN** later operations in that run SHALL continue using the same repository metadata and MUST NOT redirect the existing workspace

#### Scenario: A later run resolves updated metadata

- **WHEN** repository `web` is updated before another workflow run starts for an Issue bound to `web`
- **THEN** that later run SHALL resolve and use the updated `web` metadata

### Requirement: Workspace and review operations stay in the target repository

The Runner SHALL materialize exactly one repository for an Issue workflow from the target Git URL and SHALL create its run branch from the target base branch. Before reusing a workspace, the Runner MUST verify that it belongs to the requested Issue, target repository, and workflow run. Diff, commit, commit-diff, file-content, status, cleanup, and rebase operations SHALL act only on that target workspace. An inaccessible target, missing target base branch, or workspace identity mismatch SHALL fail without using or modifying another repository workspace.

#### Scenario: Materialize the selected repository

- **WHEN** an Issue bound to `web` starts with target base branch `develop`
- **THEN** the Runner SHALL materialize the `web` repository and create the workflow branch from `develop`

#### Scenario: Read review data from the target workspace

- **WHEN** an Issue bound to `web` has a materialized workflow workspace
- **THEN** its diff, commits, commit diffs, file content, and status SHALL be read from that `web` workspace only

#### Scenario: Rebase uses the target base branch

- **WHEN** rebase is requested without an explicit base branch for an Issue bound to `web`
- **THEN** rebase SHALL operate in the `web` workspace against the base branch resolved for that workflow run

#### Scenario: Reject a mismatched workspace

- **WHEN** a candidate workspace belongs to another repository, Issue, or workflow run
- **THEN** the Runner SHALL reject reuse before Issue work executes and MUST NOT modify the mismatched workspace

#### Scenario: An inaccessible target does not fall back

- **WHEN** the Runner cannot access the target repository or its configured base branch
- **THEN** workspace preparation SHALL fail and MUST NOT clone the Project default or infer another base branch

### Requirement: Delivery completes in the target repository

Every built-in delivery path SHALL publish and integrate changes only in the Issue's target repository. Local integration SHALL deliver to the target base branch used by the workflow run. Pull-request delivery SHALL create, inspect, update, and merge the pull request in the target repository with that base branch as its destination. Delivery MUST NOT infer the Project default or another repository from an Issue number, branch name, or pull-request number.

#### Scenario: Local integration delivers to the target

- **WHEN** an Issue bound to `web` reaches Integrate under a local delivery workflow
- **THEN** all integration actions SHALL run in the `web` workspace and deliver to the `web` base branch used by that run

#### Scenario: Pull-request delivery uses the target

- **WHEN** an Issue bound to `web` reaches pull-request publication and merge
- **THEN** pull-request operations SHALL use the repository and base branch represented by the run's `web` target context

#### Scenario: Equal pull-request numbers remain repository-scoped

- **WHEN** repositories `server` and `web` each contain the same pull-request number for different Issues
- **THEN** each Issue's pull-request operations SHALL resolve that number only in its own target repository

### Requirement: Repository coordination isolates unrelated Issues

Mohist SHALL keep workflow workspaces and run branches distinct between Issues. Concurrent delivery to the same Project repository SHALL be protected from overlapping repository side effects. A blocked or failed delivery in one repository MUST NOT prevent delivery in another repository from proceeding solely because both repositories belong to the same Project. Same-named repositories in different Projects SHALL remain independent.

#### Scenario: Issues in the same repository integrate serially

- **WHEN** two Issues in one Project target repository `server` and reach Integrate concurrently
- **THEN** their repository delivery side effects SHALL NOT overlap and each Issue SHALL retain its own workspace and run branch

#### Scenario: Issues in different repositories integrate independently

- **WHEN** delivery for repository `server` is blocked or fails while an Issue targeting `web` is also ready to deliver and execution capacity is available
- **THEN** the `web` delivery SHALL proceed independently and its repository state and result SHALL remain unaffected by the `server` delivery

#### Scenario: Same-named repositories in different Projects remain isolated

- **WHEN** two Projects each declare a repository named `server`
- **THEN** execution and delivery coordination for one Project's `server` repository SHALL NOT block or redirect the other Project's repository
