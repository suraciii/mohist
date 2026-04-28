## ADDED Requirements

### Requirement: Rebase API 端点

Server SHALL 提供 `POST /api/issues/:number/rebase` 端点，允许用户主动将 issue 分支同步到最新 master。端点 SHALL 执行前置条件检查，然后按 stage 执行差异化的 rebase 行为。

#### Scenario: 前置条件检查通过 — plan stage

- **WHEN** 用户请求 `POST /api/issues/:number/rebase`
- **AND** issue 存在且 stage 为 `plan`
- **AND** worktree 存在
- **AND** agent 当前未运行（无 active agent session）
- **THEN** 系统执行 `git fetch` 获取最新远程分支
- **AND** 调用 `worktreeManager.canFastForward()` 检查是否需要 rebase

#### Scenario: 前置条件检查通过 — build stage

- **WHEN** 用户请求 `POST /api/issues/:number/rebase`
- **AND** issue 存在且 stage 为 `build`
- **AND** worktree 存在
- **AND** agent 当前未运行
- **THEN** 系统执行与 plan stage 相同的 fetch 和 fast-forward 检查

#### Scenario: 前置条件检查通过 — review stage

- **WHEN** 用户请求 `POST /api/issues/:number/rebase`
- **AND** issue 存在且 stage 为 `review`
- **AND** worktree 存在
- **AND** agent 当前未运行
- **THEN** 系统执行 fetch 和 fast-forward 检查

#### Scenario: 前置条件检查通过 — done stage

- **WHEN** 用户请求 `POST /api/issues/:number/rebase`
- **AND** issue 存在且 stage 为 `done`
- **THEN** 系统委托给 MergeQueue retry 流程（等同现有 retry-merge）
- **AND** 无需 agent 未运行检查（done 阶段本身无 agent）

#### Scenario: worktree 不存在时拒绝

- **WHEN** 用户请求 `POST /api/issues/:number/rebase`
- **AND** issue 对应的 worktree 不存在
- **THEN** 返回 400 错误
- **AND** 错误信息包含 "Worktree not found"

#### Scenario: agent 运行中时拒绝

- **WHEN** 用户请求 `POST /api/issues/:number/rebase`
- **AND** issue 有 active agent session（stage 为 plan/build/review 且 agent 正在运行）
- **THEN** 返回 409 Conflict
- **AND** 错误信息包含 "Agent is running"

#### Scenario: 不支持的 stage 时拒绝

- **WHEN** 用户请求 `POST /api/issues/:number/rebase`
- **AND** issue stage 为 `backlog` 或 `explore`
- **THEN** 返回 400 错误
- **AND** 错误信息包含 "Rebase not available in current stage"

### Requirement: Rebase API 返回值

`POST /api/issues/:number/rebase` SHALL 返回 JSON 响应，包含 rebase 操作的结果。

#### Scenario: 已是最新无需 rebase

- **WHEN** rebase 请求通过前置条件检查
- **AND** `canFastForward()` 返回 true（分支已是最新）
- **THEN** 返回 200，body 为 `{ rebased: false, message: "Already up to date" }`

#### Scenario: Rebase 成功

- **WHEN** rebase 请求通过前置条件检查
- **AND** `canFastForward()` 返回 false
- **AND** `rebaseOntoMaster({ abortOnConflict: true })` 成功
- **THEN** 返回 200，body 为 `{ rebased: true, message: "Rebase successful" }`

#### Scenario: Rebase 有冲突

- **WHEN** rebase 请求通过前置条件检查
- **AND** `rebaseOntoMaster({ abortOnConflict: true })` 因冲突失败
- **THEN** 系统执行 `git rebase --abort` 回到 rebase 前状态
- **AND** 返回 409 Conflict，body 为 `{ rebased: false, conflicts: ["file1.ts", "file2.ts"], message: "Rebase aborted due to conflicts" }`

### Requirement: Plan stage rebase 后触发 re-self-review

当 issue 处于 plan stage 且 rebase 成功时，SHALL 触发 agent re-self-review 以基于最新 master 更新设计。

#### Scenario: Plan rebase 成功后触发 re-self-review

- **WHEN** issue stage 为 `plan`
- **AND** rebase 成功完成（`rebased: true`）
- **THEN** 系统向 agent session 注入消息，包含 "master has new changes, check if design/tasks can leverage them"
- **AND** 触发 agent 重新执行 self-review
- **AND** 更新 approval gate 的 output

### Requirement: Build stage rebase 后清除 checkpoint

当 issue 处于 build stage 且 rebase 成功时，SHALL 清除 build checkpoint，因为已完成的 tasks 产出可能已被 rebase 改变。

#### Scenario: Build rebase 成功后清除 checkpoint

- **WHEN** issue stage 为 `build`
- **AND** rebase 成功完成（`rebased: true`）
- **THEN** 系统清除 build checkpoint（tasks.json 中所有 task 状态重置为 pending）
- **AND** 返回信息包含提示 "Checkpoint cleared, resume pipeline to rebuild"
- **AND** 用户需手动 resume pipeline

### Requirement: Review stage rebase 后执行 build verify

当 issue 处于 review stage 且 rebase 成功时，SHALL 执行 build verify 并更新 Changed Files diff。

#### Scenario: Review rebase 成功后 build verify

- **WHEN** issue stage 为 `review`
- **AND** rebase 成功完成（`rebased: true`）
- **THEN** 系统在 worktree 中执行 build verify（`npm run build` 或等效命令）
- **AND** 返回值包含 `buildPassed` 字段指示 build 结果
- **AND** 更新 Changed Files diff 基于最新 master

#### Scenario: Review rebase 后 build verify 失败

- **WHEN** review stage rebase 成功
- **AND** build verify 失败
- **THEN** 返回 200，body 包含 `{ rebased: true, buildPassed: false, message: "Rebase successful but build verification failed" }`
