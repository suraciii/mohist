## MODIFIED Requirements

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态，基于 Hono 框架实现。

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态
- **AND** 响应包含 `version`（string）、`gitHash`（string | null）、`sourceHead`（string | null）、`upToDate`（boolean）字段
- **AND** `sourceHead` 为 source mode 下实时 `git rev-parse HEAD` 的结果，非 source mode 为 `null`
- **AND** `upToDate` 为 `true` 当 `sourceHead` 等于 `gitHash` 或 `sourceHead` 为 `null`；否则为 `false`

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息
