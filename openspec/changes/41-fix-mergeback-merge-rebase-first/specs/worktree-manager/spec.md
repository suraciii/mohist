## MODIFIED Requirements

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

#### Scenario: Rebase 冲突不清理

- **WHEN** rebase 产生冲突且 agent 无法解决（blocked 状态）
- **THEN** worktree 保留在 rebase 冲突状态
- **AND** 用户可以手动在 worktree 中解决冲突后触发 retry

## ADDED Requirements

### Requirement: WorktreeManager 支持 rebase onto master

WorktreeManager SHALL 提供 `rebaseOntoMaster()` 方法，在 worktree 内将 issue 分支 rebase 到最新的 base branch 上。

#### Scenario: Rebase 无冲突

- **WHEN** 调用 `rebaseOntoMaster(projectPath, projectName, issueNumber, baseBranch)`
- **AND** issue 分支与最新 base branch 无冲突
- **THEN** 系统先 fetch origin 的 base branch
- **AND** 在 worktree 内执行 `git rebase origin/<baseBranch>`
- **AND** 返回 `{ success: true, conflicts: [] }`

#### Scenario: Rebase 有冲突

- **WHEN** 调用 `rebaseOntoMaster()`
- **AND** rebase 过程中检测到冲突
- **THEN** 返回 `{ success: false, conflicts: ['path/to/file1.ts', 'path/to/file2.ts'] }`
- **AND** worktree 保持 rebase 中间状态（unmerged files）
- **AND** 不自动 abort rebase

#### Scenario: Abort rebase

- **WHEN** 调用 `abortRebase(projectName, issueNumber)`
- **AND** worktree 处于 rebase 中间状态
- **THEN** 系统执行 `git rebase --abort`
- **AND** 分支恢复到 rebase 前的状态

#### Scenario: Continue rebase

- **WHEN** 调用 `continueRebase(projectName, issueNumber)`
- **AND** 冲突文件已被 resolve 并 staged
- **THEN** 系统执行 `git rebase --continue`
- **AND** 如果还有冲突，返回 `{ success: false, conflicts: [...] }`
- **AND** 如果无更多冲突，返回 `{ success: true, conflicts: [] }`

### Requirement: WorktreeManager mergeBack 使用 fast-forward only

`mergeBack()` 方法 SHALL 仅执行 fast-forward 合并，不再使用 `git merge --no-edit` 的三方合并。

#### Scenario: Fast-forward 合并

- **WHEN** 调用 `mergeBack()` 且 issue 分支是 base branch HEAD 的 descendant
- **THEN** 系统在 projectPath 执行 `git checkout <baseBranch>`
- **AND** 执行 `git merge --ff-only <branch>`
- **AND** 返回 `{ success: true }`

#### Scenario: Fast-forward 不可行

- **WHEN** 调用 `mergeBack()` 且 fast-forward 不可行
- **THEN** 返回 `{ success: false, message: "Fast-forward not possible, rebase required" }`
- **AND** 不修改 base branch
