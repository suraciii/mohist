## MODIFIED Requirements

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

## ADDED Requirements

### Requirement: WorktreeManager 支持 WIP commit 操作
WorktreeManager SHALL 支持在 issue worktree 中创建、查询 WIP commit。

#### Scenario: 创建 WIP commit
- **WHEN** 系统调用 `createWipCommit(worktreePath, taskId, attemptNumber)`
- **AND** worktree 中存在未提交的变更
- **THEN** 系统执行 `git add -A && git commit -m "WIP: T-XXX timeout (attempt N)"`
- **AND** 返回 commit hash
- **AND** commit author 为 `mohist-wip <mohist@wip>`

#### Scenario: 无变更时跳过 WIP commit
- **WHEN** 系统调用 `createWipCommit(worktreePath, taskId, attemptNumber)`
- **AND** worktree 中没有未提交的变更
- **THEN** 系统返回 `null`（不创建 commit）

#### Scenario: 查询 task 的最新 WIP commit
- **WHEN** 系统调用 `findWipCommit(worktreePath, taskId)`
- **THEN** 系统在 git log 中搜索匹配 `"WIP: T-XXX timeout*"` 的最新 commit
- **AND** 返回 `{ hash, message, changedFiles, diffStat }` 或 `null`

#### Scenario: 获取 WIP commit 的 diff 摘要
- **WHEN** 系统调用 `getWipDiffSummary(worktreePath, taskId)`
- **AND** WIP commit 存在
- **THEN** 系统执行 `git diff --stat <wip-hash>^..<wip-hash>`
- **AND** 返回变更文件列表和每个文件的增删行数
