## ADDED Requirements

### Requirement: MergeQueue 单路径合并 — rebase-first, FF-only

MergeQueue SHALL 使用唯一的单路径合并流程。所有合并入口仅通过 `enqueue()`。`processItem()` SHALL 按以下顺序执行：

1. 检查 FF（`canFastForward`）— 如果分支已线性领先于 baseBranch，直接 FF merge
2. 如果不能 FF，执行 `rebaseOntoMaster(abortOnConflict: false)` — rebase 留下冲突标记在 worktree 中
3. 如果 rebase 冲突，retry 一次（fetch fresh master 后重新 rebase）
4. 仍然冲突 → 调用 `resolveConflicts` delegate → agent 解决冲突 → `rebaseContinue()`
5. agent 成功后 → FF merge → build verify
6. agent 失败 → Blocked

#### Scenario: 分支已线性领先，直接 FF merge

- **WHEN** issue 被 enqueue 到 MergeQueue
- **AND** `canFastForward()` 返回 true（分支 mo/issue-N 是 baseBranch 的 ancestor）
- **THEN** 系统直接执行 `git merge --ff-only mo/issue-N` 到 baseBranch
- **AND** 执行 build verification
- **AND** 成功后标记为 `Merged`，清理 worktree

#### Scenario: 不能 FF，rebase 成功后 FF merge

- **WHEN** issue 被 enqueue 到 MergeQueue
- **AND** `canFastForward()` 返回 false
- **AND** `rebaseOntoMaster()` 成功（无冲突）
- **THEN** 系统执行 FF merge 到 baseBranch
- **AND** 执行 build verification
- **AND** 成功后标记为 `Merged`，清理 worktree

#### Scenario: Rebase 冲突，重试一次成功

- **WHEN** issue 被 enqueue 到 MergeQueue
- **AND** `canFastForward()` 返回 false
- **AND** `rebaseOntoMaster(abortOnConflict: false)` 遇到冲突
- **THEN** 系统先 abort rebase
- **AND** fetch fresh master（`git fetch origin`）
- **AND** 重新执行 `rebaseOntoMaster()`
- **AND** 如果第二次 rebase 成功，继续 FF merge

#### Scenario: Rebase 冲突重试仍冲突，agent 解决成功

- **WHEN** rebase 重试一次后仍然冲突
- **THEN** 系统设置 mergeState 为 `Resolving`
- **AND** 调用 `resolveConflicts` delegate
- **AND** agent 通过直接 ACP session 解决 `<<<<<<<` 冲突标记
- **AND** agent 执行 `git rebase --continue`（通过 `rebaseContinue()`）
- **AND** rebase continue 成功后，执行 FF merge → build verify → Merged

#### Scenario: Agent 冲突解决失败

- **WHEN** agent 解决冲突的 ACP session 失败（超时、错误）
- **THEN** 系统设置 mergeState 为 `Blocked`
- **AND** abort rebase
- **AND** emit `agent_conflict_resolution_failed` 事件
- **AND** 不自动重试，等待手动 `retry()`

#### Scenario: Build verification 失败

- **WHEN** FF merge 成功
- **AND** build verification（`npm run build`）失败
- **THEN** 系统回滚 merge（`git reset --hard HEAD~1`）
- **AND** 设置 mergeState 为 `BuildFailed`
- **AND** 不清理 worktree

#### Scenario: agent_completed 事件仅 enqueue

- **WHEN** agent 完成触发 `agent_completed` 事件
- **THEN** handler 仅调用 `mergeQueue.enqueue(projectId, issueNumber)`
- **AND** 不执行任何直接 merge 逻辑
- **AND** 不调用 `mergeBack`、`mergeMasterInWorktree` 或 pipeline re-entry

### Requirement: resolveConflicts delegate 注入

MergeQueueDeps SHALL 包含 `resolveConflicts` 回调函数。MergeQueue 在 rebase 冲突且重试失败后调用此回调，不直接依赖 agent 运行机制。Server 负责实现此回调为直接 ACP session。

#### Scenario: resolveConflicts 调用参数

- **WHEN** MergeQueue 需要解决 rebase 冲突
- **THEN** 调用 `resolveConflicts(entry, worktreePath, conflictFiles)`
- **AND** 回调返回 `{ success: true }` 表示 agent 成功解决冲突
- **AND** 回调返回 `{ success: false, error: string }` 表示 agent 解决失败

#### Scenario: resolveConflicts 使用直接 ACP session

- **WHEN** server 实现 resolveConflicts 回调
- **THEN** 回调启动一个直接的 ACP session（`agentRunner.startAcp()`）
- **AND** prompt 为冲突解决专用 prompt（列出冲突文件和冲突标记）
- **AND** agent 工作目录为 worktreePath
- **AND** agent 完成后不进入 pipeline 循环（不做 review → done cycle）

### Requirement: recoverFromDB 包含 Resolving 状态

MergeQueue 的 `recoverFromDB()` SHALL 将 `Resolving` 状态视为活跃状态，恢复后重置为 `Pending` 重新走合并流程。

#### Scenario: 重启后恢复 Resolving 状态的 issue

- **WHEN** server 重启
- **AND** DB 中有 mergeState 为 `Resolving` 的 issue
- **THEN** 系统将 issue 恢复到 merge queue 中
- **AND** 重置 mergeState 为 `Pending`
- **AND** 重新走完整合并流程（FF check → rebase → merge）

## REMOVED Requirements

### Requirement: agent_completed handler 直接合并

**Reason**: 双路径合并导致不可预测行为。`agent_completed` handler 现在仅 enqueue 到 MergeQueue。
**Migration**: `agent_completed` 事件 handler 替换为 `mergeQueue.enqueue(projectId, issueNumber)`。

### Requirement: mergeMasterInWorktree 反向合并

**Reason**: 反向 merge master 到 worktree 产生 non-FF merge commits，违反 rebase-first 原则。
**Migration**: 使用 `rebaseOntoMaster(abortOnConflict: false)` 替代。

### Requirement: runConflictResolutionStage pipeline re-entry

**Reason**: 冲突解决通过直接 ACP session 完成，不应 re-enter pipeline 的 Build → Review → Done 循环。
**Migration**: 使用 `resolveConflicts` delegate 和直接 ACP session。
