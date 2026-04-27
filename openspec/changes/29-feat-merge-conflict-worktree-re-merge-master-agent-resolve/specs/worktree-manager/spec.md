## ADDED Requirements

### Requirement: WorktreeManager 支持在 worktree 中 merge master

WorktreeManager SHALL 支持在 issue 的 worktree 中执行 `git merge master`，将 master 分支的最新变更合并到 issue 分支。

#### Scenario: worktree 中 merge master

- **WHEN** MergeQueue 需要将冲突转移到 worktree
- **THEN** WorktreeManager 在 issue 的 worktree 目录中执行 `git merge master`
- **AND** 合并结果（含冲突标记）留在 issue 分支

#### Scenario: worktree 不存在时返回错误

- **WHEN** MergeQueue 请求在 worktree 中 merge master
- **AND** 对应 issue 的 worktree 目录不存在
- **THEN** WorktreeManager 返回错误
- **AND** 不执行 merge 操作

## MODIFIED Requirements

### Requirement: WorktreeManager 在 Issue 完成后清理 worktree

WorktreeManager SHALL 在 Issue 合并完成后清理 worktree 和分支。当 issue 处于 `mergeState=resolving` 时不清理。

#### Scenario: 合并成功后清理

- **WHEN** Issue 的分支成功合并到主分支
- **THEN** 系统执行 `git worktree remove ~/.mohist/projects/{projectName}/worktrees/issue-{N}/`
- **AND** 系统执行 `git branch -d mo/issue-{N}`

#### Scenario: 合并失败不清理

- **WHEN** 合并失败（冲突等）
- **THEN** worktree 保留
- **AND** 用户可以手动解决冲突后重试

#### Scenario: 冲突解决中不清理

- **WHEN** issue.mergeState 为 `resolving`
- **THEN** worktree 保留不清理
- **AND** 冲突解决过程使用该 worktree
