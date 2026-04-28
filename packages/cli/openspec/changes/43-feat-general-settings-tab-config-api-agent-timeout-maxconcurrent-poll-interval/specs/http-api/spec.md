## MODIFIED Requirements

### Requirement: API 提供配置接口

Server SHALL 提供配置管理的 RESTful API，基于 Hono 框架实现。`PUT /api/config/:key` 成功时 SHALL 在响应中返回更新后的完整 config 对象（而非仅 `{ key, value }`），便于前端乐观更新。

#### Scenario: 获取配置

- **WHEN** CLI 请求 `GET /api/config`
- **THEN** 返回当前配置（隐藏敏感信息）

#### Scenario: 设置配置

- **WHEN** CLI 请求 `PUT /api/config/:key` with `{ value }`
- **THEN** 更新配置值
- **AND** 响应 data 字段包含更新后的完整 config 对象（`{ agentTimeout, maxConcurrentAgents, pollInterval }`）

#### Scenario: 设置配置验证失败

- **WHEN** CLI 请求 `PUT /api/config/:key` with `{ value }`
- **AND** 值未通过验证
- **THEN** 返回 400 错误
- **AND** error 字段包含具体验证失败原因
