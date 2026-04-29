## ADDED Requirements

### Requirement: WorktreeManager 支持 worktree 状态查询

WorktreeManager SHALL 提供 `getWorktreeStatus()` 方法，返回 worktree 相对于 base branch 的 ahead/behind 信息。

#### Scenario: 查询存在的 worktree 状态

- **WHEN** 调用 `getWorktreeStatus(projectPath, projectName, issueNumber, baseBranch)`
- **AND** worktree 存在
- **THEN** 执行 `git rev-list --left-right --count <baseBranch>...<issueBranch>` 计算 ahead/behind
- **AND** 检查 `isRebaseInProgress` 状态
- **AND** 返回 `{ exists: true, branch, ahead, behind, canFastForward, isRebaseInProgress }`
- **AND** `canFastForward` 为 `behind === 0`

#### Scenario: worktree 不存在

- **WHEN** 调用 `getWorktreeStatus(projectPath, projectName, issueNumber, baseBranch)`
- **AND** worktree 不存在
- **THEN** 返回 `{ exists: false, branch: '', ahead: 0, behind: 0, canFastForward: false, isRebaseInProgress: false }`

#### Scenario: ahead/behind 计算

- **WHEN** issue 分支有 3 个 commit 领先于 base branch，base branch 有 5 个 commit 领先于 issue 分支
- **THEN** `getWorktreeStatus` 返回 `ahead: 3, behind: 5`

#### Scenario: 分支已是最新

- **WHEN** issue 分支与 base branch 完全同步
- **THEN** `getWorktreeStatus` 返回 `ahead: 0, behind: 0, canFastForward: true`

#### Scenario: rebase 进行中

- **WHEN** worktree 中有正在进行的 rebase 操作
- **THEN** `getWorktreeStatus` 返回 `isRebaseInProgress: true`
