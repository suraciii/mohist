## MODIFIED Requirements

### Requirement: Removed endpoints return 404
Endpoints that are removed (approve, resume) SHALL return HTTP 404 instead of their previous behavior. The `pause` endpoint was never implemented and returns 404 naturally (no route registered); it is no longer tracked in the "removed" list since `POST /api/issues/:number/stop` now provides the interrupt mechanism.

#### Scenario: Removed endpoint accessed
- **WHEN** a request is made to `POST /api/issues/:number/approve` or `POST /api/issues/:number/resume`
- **THEN** the response SHALL have status code 404

#### Scenario: Pause endpoint returns 404
- **WHEN** a request is made to `POST /api/issues/:number/pause`
- **THEN** the response SHALL have status code 404 (no route registered)

## ADDED Requirements

### Requirement: Force 参数支持多端点

`POST /api/issues/:number/close`、`POST /api/issues/:number/reopen`、`POST /api/issues/:number/approve`、`POST /api/issues/:number/reject` 端点 SHALL 支持 `force` query parameter。当 `force=true` 且有 agent 运行时，先执行 stop 流程再执行原操作。无 `force` 参数时保持原有 409 行为。无 agent 运行时 `force` 为 no-op。

#### Scenario: Force close 运行中的 issue
- **WHEN** CLI 请求 `POST /api/issues/:number/close?force=true`
- **AND** issue 有 agent 正在运行
- **THEN** server 先终止 agent session 并清理状态
- **AND** 然后执行 close 操作
- **AND** 返回 200

#### Scenario: Close 无 agent 时忽略 force
- **WHEN** CLI 请求 `POST /api/issues/:number/close?force=true`
- **AND** issue 无 agent 运行
- **THEN** 正常执行 close 操作
- **AND** 返回 200

#### Scenario: 无 force 参数保持 409
- **WHEN** CLI 请求 `POST /api/issues/:number/close`（无 force 参数）
- **AND** issue 有 agent 正在运行
- **THEN** 返回 409
