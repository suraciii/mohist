### Requirement: Workflow execution uses an authoritative target repository context

Starting an Issue SHALL resolve its stored target repository name against that Issue's Project and supply the resulting canonical name, Git URL, and base branch as the authoritative repository context for workflow execution. Project, Issue, workflow-profile, stage, run, or action-default variables MUST NOT replace that context with another repository. Changing the Project default MUST NOT redirect an existing Issue. If the stored target cannot be resolved, startup SHALL fail before a workflow run or workspace is created and MUST NOT fall back to the default repository.

#### Scenario: Start an Issue on an explicit target

- **WHEN** an Issue bound to `web` starts in a Project whose default repository is `server`
- **THEN** the workflow repository context SHALL name `web`
- **AND** its Git URL and base branch SHALL be resolved from the `web` declaration

#### Scenario: A default change does not redirect startup

- **WHEN** an Issue was bound to `server` and the Project later makes `web` the default
- **THEN** starting that Issue SHALL still resolve and use repository `server`

#### Scenario: Routing variables cannot override the binding

- **WHEN** configurable variables contain repository values that point to a repository other than the Issue target
- **THEN** repository operations SHALL use the authoritative context resolved from the stored target
- **AND** the configurable values MUST NOT redirect the workspace or Git operation

#### Scenario: Missing target declaration fails closed

- **WHEN** an Issue's stored target repository is not declared when startup is requested
- **THEN** startup SHALL fail with a repository-configuration error identifying that target
- **AND** no workflow run or workspace SHALL be created
- **AND** the Project default MUST NOT be substituted

### Requirement: A workflow run keeps one coherent repository context

At workflow start, Mohist SHALL resolve the Issue's stable target name from the Project's current declaration at that moment and capture one immutable repository runtime context for that workflow run. Workspace materialization, review reads, automatic rebase and recovery, local delivery, and GitHub pull-request delivery for that run MUST use the same captured canonical name, Git URL, and base branch. A Project repository metadata change after the run starts SHALL affect workflow runs created afterward and MUST NOT partially redirect or mix metadata within the existing run.

#### Scenario: Repository metadata changes before startup

- **WHEN** an Issue is bound to `web` and the Project updates `web`'s Git URL or base branch before the Issue starts
- **THEN** startup SHALL use the updated `web` metadata
- **AND** the Issue's stored target name SHALL remain `web`

#### Scenario: Repository metadata changes after startup

- **WHEN** an Issue workflow starts with repository `web` at Git URL `git@example.com:web.git` and base branch `develop`
- **AND** the Project later changes the `web` declaration while that workflow run remains active
- **THEN** every later Git operation in that run SHALL continue to use `git@example.com:web.git` and `develop`
- **AND** the run MUST NOT combine new metadata with its existing workspace

#### Scenario: A later workflow run uses updated metadata

- **WHEN** the Project updates repository `web` before a new workflow run is created for an Issue bound to `web`
- **THEN** that new run SHALL capture the updated `web` Git URL and base branch

### Requirement: Workspace materialization is bound to the target repository

The Runner SHALL materialize exactly one repository for an Issue workflow: the Git URL from the authoritative target context. It SHALL create the workflow run branch from that repository's configured base branch and SHALL keep workspace identity distinct by Project, target repository, Issue, and workflow run. Re-entering an existing workspace MUST verify that it belongs to the same target repository and run before reuse. An inaccessible repository, missing base branch, or repository identity mismatch SHALL fail without cloning, reusing, or modifying another Project repository's workspace.

#### Scenario: Materialize the selected repository

- **WHEN** an Issue bound to `web` starts with Git URL `git@example.com:web.git` and base branch `develop`
- **THEN** the Runner SHALL clone `git@example.com:web.git`
- **AND** it SHALL create the run branch from `develop`

#### Scenario: Do not reuse a workspace for another repository

- **WHEN** a workspace path contains a clone or marker for a different repository or workflow run
- **THEN** the Runner MUST NOT treat that workspace as the requested target workspace
- **AND** preparation SHALL fail with an identity error before Issue work executes
- **AND** the mismatched workspace SHALL remain unmodified

#### Scenario: An inaccessible target does not fall back

- **WHEN** the Runner cannot access the target repository or its configured base branch
- **THEN** workspace preparation SHALL fail with an actionable repository error
- **AND** it MUST NOT clone the Project default or use an implicit `main` branch

### Requirement: Review and maintenance operations stay in the target workspace

Issue diff, commit list, commit diff, file-content, workspace-status, cleanup, and rebase operations SHALL act only on the workspace and run branch created for the Issue's target repository. Operations that derive a base branch SHALL use the workflow run's captured target base branch. A missing target declaration at workflow start SHALL produce a repository-configuration conflict, while a missing or unverifiable persisted workspace for an existing run SHALL produce a workspace-unavailable result. Neither condition can cause the system to synthesize a default repository, legacy branch, or alternative workspace; such fallback MUST NOT occur. Cleanup SHALL rely on the persisted workflow-run workspace identity and SHALL NOT require a live repository declaration.

#### Scenario: Diff and commits come from the target repository

- **WHEN** an Issue bound to `web` has a materialized workflow workspace
- **THEN** its diff, commit list, commit diff, and file-content reads SHALL inspect that `web` workspace and run branch
- **AND** they MUST NOT read the `server` repository or another Issue workspace

#### Scenario: Rebase defaults to the target base branch

- **WHEN** a user requests rebase without an explicit base branch for an Issue bound to `web`
- **THEN** rebase SHALL use the `web` base branch captured by the workflow run

#### Scenario: A non-empty explicit rebase base is operation-scoped

- **WHEN** a user requests rebase with an explicit non-empty base-branch override
- **THEN** that override SHALL apply only to that rebase operation in the target workspace
- **AND** it MUST NOT change the Issue binding or the delivery target used by Integrate

#### Scenario: Missing workspace returns a workspace result

- **WHEN** an existing workflow run has no persisted or verifiable target workspace
- **AND** a user requests diff, commits, file content, status, or rebase
- **THEN** the operation SHALL return a workspace-unavailable result
- **AND** it MUST NOT derive another path or repository from the Project default

#### Scenario: Cleanup remains run-scoped after terminal repository deletion

- **WHEN** a terminal Issue's repository declaration has been deleted but its persisted workflow workspace still exists
- **THEN** cleanup SHALL remove only that workflow run's workspace
- **AND** cleanup SHALL NOT require or substitute another repository declaration

### Requirement: Delivery completes in the target repository

Every built-in delivery path SHALL publish and integrate changes only in the Issue's target repository. Local integration SHALL rebase, verify, squash, and push to the base branch captured in the workflow run's repository context. GitHub pull-request delivery SHALL create or reuse, inspect, mark ready, and merge the pull request in that repository with the captured base branch as its target. Neither delivery path SHALL infer the Project default or an unrelated repository from matching branch or pull-request numbers.

#### Scenario: Local Integrate pushes the target repository

- **WHEN** an Issue bound to `web` reaches Integrate under the local workflow profile
- **THEN** all integration Git actions SHALL run in the `web` workspace
- **AND** the delivered commit SHALL be pushed to the `web` base branch captured by the workflow run

#### Scenario: GitHub delivery uses the target repository

- **WHEN** an Issue bound to `web` reaches pull-request publication and merge
- **THEN** pull-request operations SHALL execute against the repository represented by the `web` workspace
- **AND** the pull request base SHALL be the `web` base branch captured by the workflow run

#### Scenario: Identical pull-request numbers remain repository-scoped

- **WHEN** repositories `server` and `web` each contain pull request number 42 for different Issues
- **THEN** each Issue's pull-request operations SHALL resolve number 42 only in its own target repository

### Requirement: Repository coordination isolates unrelated Issues

Issues SHALL have distinct workflow workspaces and run branches. Integrate coordination SHALL serialize delivery side effects for Issues targeting the same canonical repository resource, while Issues targeting different repository resources SHALL NOT block one another solely because they belong to the same Project. Repository coordination SHALL be scoped by Project and canonical repository name so case variants share one scope and equal names in different Projects remain independent.

#### Scenario: Issues in the same repository integrate serially

- **WHEN** two Issues in one Project target repository `server` and reach Integrate concurrently
- **THEN** their repository delivery side effects SHALL be serialized
- **AND** each Issue SHALL retain its own workspace and run branch

#### Scenario: Issues in different repositories integrate independently

- **WHEN** one Issue targets `server` and another targets `web` in the same Project
- **AND** both reach Integrate while execution capacity is available
- **THEN** repository coordination SHALL allow both Integrate stages to progress independently
- **AND** neither repository's lock, remote, branch, pull request, or failure state SHALL be shared with the other

#### Scenario: Equal repository names in different Projects remain isolated

- **WHEN** two Projects each declare a repository named `server`
- **THEN** execution and integration coordination for one Project's `server` repository SHALL NOT block or redirect the other Project's repository
