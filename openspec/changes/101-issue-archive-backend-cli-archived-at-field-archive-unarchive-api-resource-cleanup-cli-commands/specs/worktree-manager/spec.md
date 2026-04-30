## ADDED Requirements

### Requirement: WorktreeManager 支持归档清理

WorktreeManager SHALL 提供按 issue number 清理 worktree 和分支的方法，供归档流程调用。

#### Scenario: 归档清理存在的 worktree

- **WHEN** 调用 worktree 清理方法并传入 issue number
- **AND** `~/.mohist/projects/{projectName}/worktrees/issue-{N}/` 存在
- **THEN** 系统执行 `git worktree remove` 移除该目录
- **AND** 系统执行 `git branch -d mo/issue-{N}` 删除对应分支
- **AND** 返回成功结果

#### Scenario: 归档清理不存在的 worktree

- **WHEN** 调用 worktree 清理方法并传入 issue number
- **AND** 对应的 worktree 目录不存在
- **THEN** 系统跳过清理，不报错
- **AND** 返回跳过结果（含指示 worktree 不存在的信息）

#### Scenario: 归档清理时分支已删除

- **WHEN** 调用 worktree 清理方法并传入 issue number
- **AND** worktree 目录存在但分支 `mo/issue-{N}` 已不存在
- **THEN** 系统移除 worktree 目录
- **AND** 跳过分支删除，不报错
