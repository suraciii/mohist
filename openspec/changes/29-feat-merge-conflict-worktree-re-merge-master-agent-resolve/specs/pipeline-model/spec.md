## ADDED Requirements

### Requirement: BUILD stage识别 mergeState=resolving 走冲突解决路径

当 issue 的 `mergeState` 为 `resolving` 时，BUILD stage SHALL 使用 conflict resolution prompt 替代普通 build prompt。Agent 在 worktree 中解决冲突并 commit。

#### Scenario: 冲突解决 prompt

- **WHEN** WorkflowController 执行 BUILD stage
- **AND** issue.mergeState 为 `resolving`
- **THEN** 系统使用 conflict resolution prompt 替代普通 build prompt
- **AND** prompt 包含所有冲突文件路径列表
- **AND** prompt 说明 `<<<<<<< HEAD` 为 master 变更，`>>>>>>> mo/issue-N` 为 issue 变更
- **AND** prompt 要求保留双方变更，不丢弃任何一方

#### Scenario: Agent 解决冲突后 commit

- **WHEN** Agent 在 worktree 中解决所有冲突
- **THEN** Agent 执行 `git add` 暂存解决后的文件
- **AND** Agent commit 解决结果
- **AND** issue.mergeState 变为 `pending`（等待后续 stage 完成后重新入队）

#### Scenario: Agent 冲突解决失败

- **WHEN** Agent 在 worktree 中未能解决冲突（build 验证不通过）
- **THEN** Agent 重试解决（在 BUILD stage 内循环）
- **AND** 重试次数受 BUILD stage 的内置限制约束

#### Scenario: 冲突解决后跳过 approval gate

- **WHEN** BUILD stage 完成 conflict resolution
- **AND** issue 的 `mergeState` 从 `resolving` 变为 `pending`
- **THEN** 后续 stage（check）正常推进
- **AND** 冲突解决的 build stage 不需要用户 approval（跳过 gate）
