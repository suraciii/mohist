## ADDED Requirements

### Requirement: MergeQueue 冲突检测与 worktree 反向 merge

当 MergeQueue 执行 mergeBack 遇到 git 冲突时，系统 SHALL abort master 上的 merge（恢复 master 干净），然后在 issue 的 worktree 中执行 `git merge master`，将冲突转移到 worktree。

#### Scenario: mergeBack 遇到冲突

- **WHEN** MergeQueue 对 issue 执行 `git merge mo/issue-N` 返回冲突
- **THEN** 系统在 master 上执行 `git merge --abort`，恢复 master 为干净状态
- **AND** 系统在 issue 的 worktree 目录中执行 `git merge master`
- **AND** 冲突留在 worktree 的 issue 分支上

#### Scenario: worktree 中 merge master 也冲突

- **WHEN** MergeQueue 在 worktree 中执行 `git merge master` 也返回冲突
- **THEN** 这是预期行为，冲突标记留在 worktree 中待 agent 解决

#### Scenario: worktree 中 merge master 无冲突

- **WHEN** MergeQueue 在 worktree 中执行 `git merge master` 无冲突
- **THEN** 系统 commit merge 结果
- **AND** 将 issue 重新 enqueue MergeQueue

### Requirement: 冲突后 issue 状态转换

MergeQueue 检测到冲突并完成 worktree 反向 merge 后，issue SHALL 回退到 build stage，`mergeState` 设为 `resolving`。

#### Scenario: 冲突触发状态回退

- **WHEN** MergeQueue 完成冲突转移到 worktree
- **THEN** issue.stage 设为 `build`
- **AND** issue.mergeState 设为 `resolving`
- **AND** 系统 emit `merge_conflict_requiring_resolution` 事件，payload 包含 `{ issueId, projectId, conflictFiles }`

### Requirement: MergeState 枚举新增 resolving

`MergeState` 枚举 SHALL 包含 `resolving` 值，表示 issue 处于冲突自动解决中。

#### Scenario: MergeState 枚举值

- **WHEN** 系统检查 MergeState 枚举
- **THEN** 枚举包含 `pending | merging | merged | build-failed | conflict | resolving | blocked`

### Requirement: 冲突解决最大重试次数

冲突自动解决 SHALL 最多重试 3 次。超过后 issue 标记为 `blocked`，等待人工介入。

#### Scenario: 首次冲突自动解决

- **WHEN** issue 首次进入 `mergeState=resolving`
- **THEN** 系统 launch agent 解决冲突
- **AND** conflictRetryCount 设为 1

#### Scenario: 重试次数未达上限

- **WHEN** issue 冲突解决后重新 mergeBack 再次冲突
- **AND** conflictRetryCount < 3
- **THEN** issue 再次进入 `mergeState=resolving`
- **AND** conflictRetryCount 递增

#### Scenario: 重试次数达到上限

- **WHEN** issue 冲突解决后重新 mergeBack 再次冲突
- **AND** conflictRetryCount >= 3
- **THEN** issue.mergeState 设为 `blocked`
- **AND** 系统 emit `merge_blocked` 事件，等待人工介入

### Requirement: 冲突解决完成后重新入队

Agent 解决冲突并 commit 后，issue 按 build → check → done 正常推进。`agent_completed` 事件触发时，系统 SHALL 重新将 issue enqueue MergeQueue。

#### Scenario: 冲突解决后正常推进

- **WHEN** Agent 在 worktree 中解决冲突并 commit
- **THEN** issue 走 build → check → done 流程
- **AND** `agent_completed` 事件触发 MergeQueue enqueue
- **AND** MergeQueue 重新执行 mergeBack（此时应无冲突）

#### Scenario: 重新 mergeBack 成功

- **WHEN** 冲突解决后的 issue 重新 mergeBack
- **THEN** 合并成功，worktree 清理
- **AND** issue.mergeState 设为 `merged`
