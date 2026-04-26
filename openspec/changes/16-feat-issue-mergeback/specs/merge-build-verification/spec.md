## ADDED Requirements

### Requirement: 合并后执行构建验证

MergeQueue 在 mergeBack 成功后 SHALL 在 base branch 上执行构建验证，确保合并后的代码可构建。

#### Scenario: 合并成功后执行构建

- **WHEN** mergeBack 返回 success（分支已合并到 baseBranch）
- **THEN** 在项目根目录执行 `npm run build`
- **AND** 构建执行超时为 5 分钟

#### Scenario: 构建验证通过

- **WHEN** `npm run build` 退出码为 0
- **THEN** 构建验证通过
- **AND** MergeQueue 继续后续流程（清理 worktree，标记 merged）

#### Scenario: 构建验证失败自动回滚

- **WHEN** `npm run build` 退出码非 0
- **THEN** 在项目根目录执行 `git reset --hard HEAD~1` 回滚合并提交
- **AND** issue 的 `mergeState` 设置为 `build-failed`
- **AND** 保留 worktree 不清理
- **AND** EventBus emit `merge_failed` 事件，payload 包含 `{ issueNumber, reason: 'build-failed', message: '<build error output>' }`

#### Scenario: 构建超时

- **WHEN** `npm run build` 执行超过 5 分钟
- **THEN** 终止构建进程
- **AND** 执行 `git reset --hard HEAD~1` 回滚合并
- **AND** issue 的 `mergeState` 设置为 `build-failed`
- **AND** 失败消息包含 "Build verification timed out"

### Requirement: 构建验证在 worktree 项目路径执行

构建验证 SHALL 在项目的主仓库路径（project.path）执行，而非 worktree 路径，因为 mergeBack 已经将代码合并到 baseBranch。

#### Scenario: 构建在正确路径执行

- **WHEN** 构建验证启动
- **THEN** 工作目录为 `project.path`（主仓库路径）
- **AND** baseBranch 已经 checkout 到该路径

#### Scenario: 构建前确认当前分支

- **WHEN** 构建验证启动
- **THEN** 验证当前分支为 baseBranch
- **AND** 如果当前分支不是 baseBranch，返回错误不执行构建
