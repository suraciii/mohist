## ADDED Requirements

### Requirement: MergeQueue 在 merge 前先 rebase issue 分支到最新 master

MergeQueue SHALL 在执行 mergeBack 之前，先在 worktree 内将 issue 分支 rebase 到最新的 `origin/<baseBranch>`。rebase 成功后，mergeBack SHALL 使用 `git merge --ff-only` 进行 fast-forward 合并。

#### Scenario: rebase 成功后 fast-forward merge

- **WHEN** MergeQueue 处理一个 pending 状态的 issue
- **THEN** 系统先将 issue 的 worktree 分支 rebase 到最新 `origin/<baseBranch>`
- **AND** rebase 成功后执行 `git checkout <baseBranch>` + `git merge --ff-only <branch>`
- **AND** mergeState 从 `pending` → `rebasing` → `merging` → `merged`

#### Scenario: rebase 产生冲突

- **WHEN** MergeQueue 处理一个 pending 状态的 issue
- **AND** rebase 过程中检测到 conflict
- **THEN** 系统执行 `git rebase --abort` 回退 rebase
- **AND** mergeState 设为 `conflict`
- **AND** 发出 `rebase_conflict` 事件，payload 包含冲突文件列表和 issue 信息
- **AND** worktree 保留（不清理）

#### Scenario: rebase 前自动提交 worktree 内未提交的更改

- **WHEN** MergeQueue 处理一个 pending 状态的 issue
- **AND** worktree 内存在未提交的更改
- **THEN** 系统先执行 `git add` + `git commit` 将未提交更改提交到 issue 分支
- **AND** 然后执行 rebase 操作

#### Scenario: issue 分支无新 commit 时跳过 merge

- **WHEN** MergeQueue 处理一个 pending 状态的 issue
- **AND** issue 分支相对于 baseBranch 没有新 commit
- **THEN** 系统直接清理 worktree（不执行 rebase 和 merge）
- **AND** mergeState 设为 `merged`

### Requirement: MergeState 类型包含 rebase 相关状态

`MergeState` 类型 SHALL 包含以下值：`'pending' | 'rebasing' | 'merging' | 'merged' | 'build-failed' | 'conflict' | 'blocked'`。

#### Scenario: MergeState 包含 rebasing 状态

- **WHEN** MergeQueue 正在对 issue 执行 rebase 操作
- **THEN** issue 的 mergeState 为 `'rebasing'`

#### Scenario: MergeState 包含 blocked 状态

- **WHEN** issue 的 rebase 失败且 auto-retry 也已耗尽
- **THEN** issue 的 mergeState 为 `'blocked'`
- **AND** 用户可在 UI 上看到 blocked 状态并手动触发重试
