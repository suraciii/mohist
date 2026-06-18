# OpenSpec Capability: worktree-manager

### Requirement: WorktreeManager 管理每个 Issue 的隔离工作区

WorktreeManager SHALL 为每个 Issue 创建独立的 git worktree 作为隔离工作区，基于项目的 `baseBranch` 对应的远程分支创建。

#### Scenario: 创建 worktree

- **WHEN** 用户执行 `mo issue start <number>`
- **THEN** 系统执行智能 fetch（30 分钟内 fetch 过则跳过）
- **AND** 系统在 `~/.mohist/projects/{projectName}/worktrees/issue-{N}/` 创建 git worktree
- **AND** 创建对应的分支 `mo/issue-{N}`，基于 `origin/<baseBranch>`（baseBranch 为项目配置的主干分支）
- **AND** 返回 worktree 路径供 Agent 使用
- **AND** `{projectName}` 经过 slug 处理（小写、空格转 `-`、去除特殊字符）

#### Scenario: origin/<baseBranch> 不存在时回退到本地分支

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** `origin/<baseBranch>` 不存在（如分支被删除或配置错误）
- **AND** 本地存在 `<baseBranch>` 分支
- **THEN** 系统基于本地 `<baseBranch>` 分支创建 worktree
- **AND** 记录警告日志："Using local branch '<baseBranch>', remote not found"

#### Scenario: origin/<baseBranch> 和本地分支都不存在

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** `origin/<baseBranch>` 不存在
- **AND** 本地 `<baseBranch>` 分支也不存在
- **THEN** 系统返回错误："Branch '<baseBranch>' not found locally or on origin"
- **AND** 不创建 worktree

#### Scenario: worktree 已存在

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** `~/.mohist/projects/{projectName}/worktrees/issue-{N}/` 已存在
- **THEN** 系统复用现有 worktree
- **AND** 不创建新分支

#### Scenario: 项目非 git 仓库

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** 项目不是 git 仓库
- **THEN** 系统返回错误提示 "Project is not a git repository"
- **AND** 不创建 worktree

#### Scenario: 无 origin remote 时回退到本地 baseBranch

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** 项目没有 origin remote（纯本地仓库）
- **AND** 本地存在 `<baseBranch>` 分支
- **THEN** 系统基于本地 `<baseBranch>` 分支创建 worktree
- **AND** 如果本地 `<baseBranch>` 分支不存在，返回错误

### Requirement: WorktreeManager 在 Issue 完成后清理 worktree

WorktreeManager SHALL 在 Issue 合并完成后清理 worktree 和分支。

#### Scenario: 合并成功后清理

- **WHEN** Issue 的分支成功合并到主分支
- **THEN** 系统执行 `git worktree remove ~/.mohist/projects/{projectName}/worktrees/issue-{N}/`
- **AND** 系统执行 `git branch -d mo/issue-{N}`

#### Scenario: 合并失败不清理

- **WHEN** 合并失败（冲突等）
- **THEN** worktree 保留
- **AND** 用户可以手动解决冲突后重试

### Requirement: WorktreeManager 支持 worktree 列表查询

WorktreeManager SHALL 支持列出当前所有活跃的 worktree。

#### Scenario: 列出 worktree

- **WHEN** 用户或系统查询 worktree 列表
- **THEN** 系统执行 `git worktree list`
- **AND** 返回所有 worktree 的路径和对应分支信息

### Requirement: WorktreeManager 支持清理无效 worktree

WorktreeManager SHALL 支持清理已删除目录的 worktree 引用。

#### Scenario: prune worktree

- **WHEN** 系统执行 prune 操作
- **THEN** 系统执行 `git worktree prune`
- **AND** 清理所有无效的 worktree 引用

### Requirement: Read-only squash mergeability preflight

`WorktreeManager` SHALL provide a mergeability preflight that verifies whether an issue candidate can be squash-merged into the current base branch using the same merge strategy as Integrate, without mutating the base branch, the issue branch, or the workflow workspace branch context. The preflight SHALL be ref-safe: it SHALL NOT check out the base branch inside the workflow workspace, and it SHALL leave the workflow workspace on its `workspace.branch`. Any branch-context-changing work the preflight needs SHALL happen in an isolated temporary workspace separate from the workflow workspace.

#### Scenario: Clean candidate reports structured mergeability

- **GIVEN** a base branch and issue candidate that can be cleanly merged with `git merge --squash <candidate>`
- **WHEN** Mohist checks squash mergeability
- **THEN** the result SHALL include `kind: "merge-ready"`, `strategy: "squash"`, `targetBranch`, `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `canMerge: true`, `conflictFiles`, and `checkedAt`
- **AND** the base branch, issue branch, and workflow workspace branch refs SHALL remain unchanged

#### Scenario: Conflicting candidate reports conflict files

- **GIVEN** a base branch and issue candidate that would fail `git merge --squash <candidate>`
- **WHEN** Mohist checks squash mergeability
- **THEN** the result SHALL have `canMerge: false`
- **AND** the result SHALL include structured conflict file evidence gathered before cleanup
- **AND** cleanup failure SHALL NOT turn a detected conflict into a passing result

#### Scenario: Preflight does not check out the base branch in the workflow workspace

- **WHEN** the mergeability preflight runs against an active workflow workspace
- **THEN** the preflight SHALL NOT run `git checkout <baseBranch>` inside the workflow workspace
- **AND** the workflow workspace SHALL remain on `workspace.branch` before and after the preflight
- **AND** any temporary checkout the preflight needs SHALL happen in an isolated workspace separate from the workflow workspace

### Requirement: Authoritative final squash merge diagnostics

`WorktreeManager` SHALL continue to treat the real Integrate squash merge as the final authority and SHALL report structured conflict evidence when that merge fails.

#### Scenario: Final merge race reports structured conflicts

- **GIVEN** a candidate passed preflight but a later race or Integrate-generated artifact commit introduces a squash merge conflict
- **WHEN** Integrate runs the authoritative `git merge --squash <candidate>` operation
- **THEN** Integrate SHALL fail the merge task
- **AND** the failure output SHALL include `targetBranch`, `strategy`, conflict files, and available `baseSha`, `candidateHeadSha`, and `mergeBaseSha`

### Requirement: Merge boundary validates source worktree cleanliness

Before the merge action begins modifying the target branch, the runner SHALL validate that the source worktree is clean. A dirty worktree at the merge boundary SHALL block the merge and produce structured dirty-worktree evidence.

#### Scenario: Clean worktree check at merge boundary

- **WHEN** `mohist/merge` is invoked for an Integrate workflow task
- **THEN** the first validation SHALL be a `git status --porcelain` check in the task workspace
- **AND** the merge SHALL NOT proceed to fetch, checkout, rebase, or push operations if the worktree is dirty

#### Scenario: Dirty worktree at merge boundary produces structured evidence

- **WHEN** `mohist/merge` detects a dirty worktree at the merge boundary
- **THEN** the failure output SHALL include the categorized file lists from `git status --porcelain`
- **AND** the failure SHALL include the phase classification `source-cleanup`
- **AND** the merge action SHALL NOT silently commit the dirty changes

#### Scenario: Merge-boundary clean check is not a stage-level check

- **WHEN** the merge action validates source worktree cleanliness
- **THEN** the validation SHALL execute inside the merge task action
- **AND** it SHALL NOT be modeled as a workflow check, stage gate, or separate approval step

### Requirement: Isolated temporary landing workspaces for branch-stable delivery

`WorktreeManager` SHALL support creating isolated temporary landing workspaces, separate from the workflow workspace, so that delivery operations which need to construct or advance a commit on the base branch can do so without switching the workflow workspace off its run branch. An isolated landing workspace SHALL be materialized as a `git clone --shared` of the workflow workspace (so the run branch's prepared commits are visible alongside the base branch), SHALL be disposable after the delivery operation without affecting the workflow workspace's object store or refs, and SHALL NOT alias the workflow workspace path. The workflow workspace SHALL remain on `workspace.branch` for the lifetime of any landing workspace it spawns.

#### Scenario: Publish lands via an isolated temporary landing workspace

- **WHEN** `integrate:publish` needs to construct the single landing commit on the base branch
- **THEN** WorktreeManager SHALL provide an isolated temporary landing workspace separate from the workflow workspace
- **AND** the landing commit, fast-forward, and push SHALL be performed in that isolated workspace
- **AND** the workflow workspace SHALL remain on `workspace.branch` throughout the publish task

#### Scenario: Landing workspace does not disturb the workflow workspace

- **WHEN** an isolated temporary landing workspace is created and later removed for a delivery operation
- **THEN** the workflow workspace path, branch, and working tree SHALL be unaffected
- **AND** the workspace SHALL remain on `workspace.branch` before, during, and after the landing workspace's lifetime

