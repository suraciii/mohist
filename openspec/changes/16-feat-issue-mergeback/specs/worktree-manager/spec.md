## MODIFIED Requirements

### Requirement: WorktreeManager 在 Issue 完成后清理 worktree

WorktreeManager SHALL 在 Issue 通过 MergeQueue 成功合并并验证构建后清理 worktree 和分支。WorktreeManager 的 `mergeBack()` 方法 SHALL NOT 被 `agent_completed` 事件直接调用，而由 MergeQueue 统一调度。

#### Scenario: MergeQueue 调用 mergeBack 成功后清理

- **WHEN** MergeQueue 调用 WorktreeManager.mergeBack() 返回 success
- **AND** 构建验证通过
- **THEN** MergeQueue 调用 WorktreeManager.remove() 执行 `git worktree remove ~/.mohist/projects/{projectName}/worktrees/issue-{N}/`
- **AND** 系统执行 `git branch -d mo/issue-{N}`

#### Scenario: mergeBack 失败不清理

- **WHEN** MergeQueue 调用 WorktreeManager.mergeBack() 返回失败（冲突等）
- **THEN** worktree 保留
- **AND** issue `mergeState` 标记为 `conflict`
- **AND** 用户可以通过 `POST /api/issues/:number/retry-merge` 重试

#### Scenario: 构建验证失败不清理

- **WHEN** mergeBack 成功但构建验证失败
- **THEN** MergeQueue 执行 `git reset --hard HEAD~1` 回滚合并
- **AND** worktree 保留
- **AND** issue `mergeState` 标记为 `build-failed`

#### Scenario: agent_completed 不直接调用 mergeBack

- **WHEN** `agent_completed` 事件触发
- **THEN** server handler 调用 `mergeQueue.enqueue()` 而非 `worktreeManager.mergeBack()`
- **AND** 不存在从 `agent_completed` 事件直接调用 `mergeBack()` 的代码路径
