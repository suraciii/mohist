## MODIFIED Requirements

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态，基于 Hono 框架实现。`GET /api/status` 和 `GET /api/health` 的响应 SHALL 包含 `version` 和 `gitHash` 字段。

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态
- **AND** 响应 data 对象包含 `version`（string，如 `"0.1.0"`）和 `gitHash`（string | null，如 `"abc1234"`）字段

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息

#### Scenario: Health 端点包含版本信息
- **WHEN** CLI 请求 `GET /api/health`
- **THEN** 返回 `{ status: "ok", timestamp: "...", version: "0.1.0", gitHash: "abc1234" }`
- **AND** `version` 和 `gitHash` 从 `getVersionInfo()` 获取
