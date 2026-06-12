## MODIFIED Requirements

### Requirement: Workspace manager 管理每个 Issue 的隔离工作区

The runner workspace manager SHALL prepare an isolated workflow workspace for each workflow run or issue from repository `gitUrl` and `baseBranch`. The workflow workspace SHALL be a runner-managed directory under `MOHIST_RUNNER_ROOT` and SHALL NOT be a git worktree attached to the user's main checkout. Workflow preparation SHALL NOT require project path or repository path configuration.

#### Scenario: 创建 workflow workspace

- **WHEN** 用户执行 `mo issue start <number>`
- **THEN** 系统 SHALL prepare or refresh a runner-owned repository cache/clone from repository `gitUrl`
- **AND** 系统 SHALL create an isolated workflow workspace directory for that issue or workflow run under `MOHIST_RUNNER_ROOT`
- **AND** the workspace SHALL checkout or materialize repository content for the configured `baseBranch`
- **AND** the system SHALL return `workspace.path` for workflow execution
- **AND** the system SHALL NOT run `git worktree add`
- **AND** the workspace path SHALL NOT be derived from a project local path

#### Scenario: origin/<baseBranch> 不存在时回退到本地分支

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** repository `baseBranch` cannot be resolved from the configured `gitUrl`
- **THEN** 系统 SHALL fail workspace preparation with a clear branch resolution error
- **AND** it SHALL NOT fall back to a user's local project branch

#### Scenario: origin/<baseBranch> 和本地分支都不存在

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** repository `baseBranch` cannot be resolved from the configured `gitUrl`
- **THEN** 系统返回错误 indicating the configured base branch cannot be found
- **AND** 不创建 workflow workspace

#### Scenario: workflow workspace 已存在

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** a workflow workspace already exists for the active run
- **THEN** 系统 SHALL reuse the existing workflow workspace for that run
- **AND** it SHALL NOT create a git worktree branch such as `mo/issue-{N}`

#### Scenario: 项目非 git 仓库

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** the Project has no local git repository path because projects are Mohist scopes
- **THEN** 系统 SHALL prepare from the selected repository `gitUrl`
- **AND** it SHALL NOT return "Project is not a git repository" based on project filesystem inspection

#### Scenario: 无 origin remote 时回退到本地 baseBranch

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** repository configuration has no usable `gitUrl`
- **THEN** 系统 SHALL reject the repository configuration
- **AND** it SHALL NOT fall back to a local base branch from a user checkout

### Requirement: Workspace manager 在 Issue 完成后清理 workflow workspace

The runner workspace manager SHALL clean up runner-managed workflow workspaces after workflow completion or explicit cleanup. Cleanup SHALL NOT run `git worktree remove` and SHALL NOT delete branches from a user's main checkout.

#### Scenario: 合并成功后清理

- **WHEN** Issue integration completes and workspace cleanup is requested or configured
- **THEN** 系统 SHALL remove the runner-managed workflow workspace directory
- **AND** 系统 SHALL NOT execute `git worktree remove`
- **AND** 系统 SHALL NOT execute `git branch -d mo/issue-{N}` in a user checkout

#### Scenario: 合并失败不清理

- **WHEN** 合并失败（冲突等）
- **THEN** workflow workspace SHALL be retained for inspection or retry unless explicit cleanup is requested
- **AND** 用户或 workflow recovery can retry inside the same workspace boundary

### Requirement: Workspace manager 支持 workspace 列表查询

The runner workspace manager SHALL support listing active workflow workspaces from runner-managed state. It SHALL NOT use `git worktree list` or expose user checkout worktree information as workflow execution state.

#### Scenario: 列出 workflow workspace

- **WHEN** 用户或系统查询 workspace 列表
- **THEN** 系统 SHALL read runner-managed workspace records or directories
- **AND** 返回 active workflow workspace paths and associated issue/run metadata
- **AND** 系统 SHALL NOT execute `git worktree list`

### Requirement: Workspace manager 支持清理无效 workspace

The runner workspace manager SHALL support pruning invalid runner-managed workflow workspace records and directories. It SHALL NOT use git worktree prune because workflow workspaces are not git worktrees attached to a main checkout.

#### Scenario: prune workflow workspace

- **WHEN** 系统执行 prune 操作
- **THEN** 系统 SHALL clean invalid runner-managed workspace records or directories
- **AND** 系统 SHALL NOT execute `git worktree prune`

### Requirement: Read-only squash mergeability preflight

The workspace manager SHALL provide a mergeability preflight that verifies whether an issue candidate can be squash-merged into the current base branch using the same merge strategy as Integrate, without mutating external repository configuration, repository cache state, or a user's checkout. The preflight SHALL run inside the workflow workspace.

#### Scenario: Clean candidate reports structured mergeability

- **GIVEN** a base branch and issue candidate in the workflow workspace that can be cleanly merged with `git merge --squash <candidate>`
- **WHEN** Mohist checks squash mergeability
- **THEN** the result SHALL include `kind: "merge-ready"`, `strategy: "squash"`, `targetBranch`, `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `canMerge: true`, `conflictFiles`, and `checkedAt`
- **AND** the base branch and issue branch refs in external checkouts SHALL remain unchanged

#### Scenario: Conflicting candidate reports conflict files

- **GIVEN** a base branch and issue candidate in the workflow workspace that would fail `git merge --squash <candidate>`
- **WHEN** Mohist checks squash mergeability
- **THEN** the result SHALL have `canMerge: false`
- **AND** the result SHALL include structured conflict file evidence gathered before cleanup
- **AND** cleanup failure SHALL NOT turn a detected conflict into a passing result

### Requirement: Authoritative final squash merge diagnostics

The workspace manager SHALL continue to treat the real Integrate squash merge as the final authority and SHALL report structured conflict evidence when that merge fails. The final merge and any conflict resolver SHALL run inside the workflow workspace and SHALL NOT use project paths, repository cache paths, or user checkout directories as cwd.

#### Scenario: Final merge race reports structured conflicts

- **GIVEN** a candidate passed preflight but a later race or Integrate-generated artifact commit introduces a squash merge conflict
- **WHEN** Integrate runs the authoritative `git merge --squash <candidate>` operation
- **THEN** Integrate SHALL fail the merge task
- **AND** the failure output SHALL include `targetBranch`, `strategy`, conflict files, and available `baseSha`, `candidateHeadSha`, and `mergeBaseSha`
- **AND** the merge failure and conflict resolution SHALL remain inside the workflow workspace

## ADDED Requirements

### Requirement: Runner owns repository cache state
Runner SHALL own local repository cache or clone state under `MOHIST_RUNNER_ROOT`. Repository cache paths SHALL be runtime implementation details and SHALL NOT be exposed as project or repository configuration or as workflow execution cwd.

#### Scenario: Cache prepared from Git URL
- **WHEN** workflow workspace preparation starts
- **THEN** runner SHALL clone or fetch repository content from `repository.gitUrl` into runner-owned cache state
- **AND** the cache path SHALL NOT be exposed as `repository.path` or used as the action work directory

#### Scenario: Workspace created from cache
- **WHEN** repository cache preparation succeeds
- **THEN** runner SHALL create a separate workflow workspace for user work
- **AND** all workflow actions SHALL execute in that workspace rather than in the cache directory
