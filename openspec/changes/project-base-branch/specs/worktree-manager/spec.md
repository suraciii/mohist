## MODIFIED Requirements

### Requirement: WorktreeManager 管理每个 Issue 的隔离工作区

WorktreeManager SHALL 为每个 Issue 创建独立的 git worktree 作为隔离工作区，基于项目的 `baseBranch` 对应的远程分支创建。

#### Scenario: 创建 worktree

- **WHEN** 用户执行 `mo issue start <number>`
- **THEN** 系统执行 `git fetch origin`
- **AND** 系统在 `~/.mohist/projects/{projectName}/worktrees/issue-{N}/` 创建 git worktree
- **AND** 创建对应的分支 `mo/issue-{N}`，基于 `origin/<baseBranch>`（baseBranch 为项目配置的主干分支）
- **AND** 返回 worktree 路径供 Agent 使用
- **AND** `{projectName}` 经过 slug 处理（小写、空格转 `-`、去除特殊字符）

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
- **THEN** 系统基于本地 `baseBranch` 分支创建 worktree
- **AND** 如果本地 `baseBranch` 分支不存在，返回错误
