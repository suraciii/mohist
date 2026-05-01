## ADDED Requirements

### Requirement: API 提供 rerun 端点

Server SHALL 提供 `POST /api/issues/:number/rerun` 端点，允许用户从当前 stage 重新启动 pipeline，同时清理 orphan sessions 和重置状态。

#### Scenario: Rerun active issue
- **WHEN** CLI 请求 `POST /api/issues/:number/rerun`
- **AND** issue 存在且无 agent 正在运行
- **THEN** orphan coder sessions 被清理为 `failed`
- **AND** 当前 stage checkpoint 被清除
- **AND** approval_state、blocked_reason 被清除，retry_count 重置为 0
- **AND** issue status 设为 `active`，stage 保持不变
- **AND** pipeline 从当前 stage 重新启动
- **AND** 返回 200，body 包含 issue 信息

#### Scenario: Rerun closed issue reopens and reruns
- **WHEN** CLI 请求 `POST /api/issues/:number/rerun`
- **AND** issue status 为 `closed`
- **THEN** issue 被 reopened（status → `active`，stage 保持不变）
- **AND** 执行完整的 rerun 流程（清理、重置、重启 pipeline）
- **AND** 返回 200

#### Scenario: Rerun issue with agent running
- **WHEN** CLI 请求 `POST /api/issues/:number/rerun`
- **AND** 有 agent 正在为该 issue 运行
- **THEN** 返回 409 Conflict
- **AND** 错误信息包含 "agent is running"

#### Scenario: Rerun nonexistent issue
- **WHEN** CLI 请求 `POST /api/issues/999/rerun`
- **AND** issue 不存在
- **THEN** 返回 404 错误

#### Scenario: Rerun issue in draft stage
- **WHEN** CLI 请求 `POST /api/issues/:number/rerun`
- **AND** issue stage 为 `draft`
- **THEN** 返回 400 错误
- **AND** 错误信息提示 draft stage 应使用 start 而非 rerun

#### Scenario: Rerun issue in done stage
- **WHEN** CLI 请求 `POST /api/issues/:number/rerun`
- **AND** issue stage 为 `done`
- **THEN** 返回 400 错误
- **AND** 错误信息提示 done stage 不支持 rerun
