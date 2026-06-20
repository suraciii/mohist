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

`WorktreeManager` SHALL provide a mergeability preflight that verifies whether an issue candidate is prepared against the latest base branch using `git merge-base --is-ancestor origin/<baseBranch> <runBranch>`, without mutating the base branch, the issue branch, or the workflow workspace branch context. The preflight SHALL be ref-safe and working-tree-free: it SHALL NOT check out the base branch inside the workflow workspace, it SHALL NOT create an isolated landing clone, it SHALL NOT run a `git merge --squash` probe, and it SHALL leave the workflow workspace on its `workspace.branch`. A candidate whose run branch already contains the latest remote base branch tip SHALL be reported as merge-ready; a candidate whose run branch does not contain the latest base tip SHALL be reported as not ready, indicating a rebase is required. Conflict file detail SHALL NOT be reported by the preflight; conflicts are surfaced by the authoritative Integrate rebase.

#### Scenario: Prepared candidate reports merge-ready

- **GIVEN** a run branch whose history already contains the latest `origin/<baseBranch>` tip
- **WHEN** Mohist checks mergeability
- **THEN** the result SHALL include `kind: "merge-ready"`, `strategy: "squash"`, `targetBranch`, `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `canMerge: true`, `conflictFiles` (empty), and `checkedAt`
- **AND** the base branch, issue branch, and workflow workspace branch refs SHALL remain unchanged

#### Scenario: Candidate behind base reports not ready

- **GIVEN** a run branch whose history does NOT contain the latest `origin/<baseBranch>` tip
- **WHEN** Mohist checks mergeability
- **THEN** the result SHALL have `canMerge: false`
- **AND** the result SHALL indicate a rebase is required
- **AND** the result SHALL NOT report conflict file detail from a probe merge

#### Scenario: Preflight does not check out the base branch or create a landing clone

- **WHEN** the mergeability preflight runs against an active workflow workspace
- **THEN** the preflight SHALL NOT run `git checkout <baseBranch>` inside the workflow workspace
- **AND** the preflight SHALL NOT create an isolated landing clone or run a `merge --squash` probe
- **AND** the workflow workspace SHALL remain on `workspace.branch` before and after the preflight

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

