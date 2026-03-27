## Requirements

### Requirement: WorktreeManager 管理每个 Issue 的隔离工作区

WorktreeManager SHALL 为每个 Issue 创建独立的 git worktree 作为隔离工作区。

#### Scenario: 创建 worktree

- **WHEN** 用户执行 `mo issue start <number>`
- **THEN** 系统在 `~/.mohist/projects/{projectName}/worktrees/issue-{N}/` 创建 git worktree
- **AND** 创建对应的分支 `mo/issue-{N}`，基于当前 HEAD
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
