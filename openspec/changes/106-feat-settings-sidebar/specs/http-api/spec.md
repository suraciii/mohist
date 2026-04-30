## MODIFIED Requirements

### Requirement: API 提供配置接口

Server SHALL 提供配置管理的 RESTful API，基于 Hono 框架实现。除现有的 `GET /api/config` 和 `PUT /api/config/:key` 外，SHALL 新增以下专用配置端点：`GET /api/config/model`、`PUT /api/config/model`、`GET /api/config/opencode-model`、`PUT /api/config/opencode-model`、`GET /api/config/log-level`、`PUT /api/config/log-level`、`GET /api/config/agent-runtime`、`PUT /api/config/agent-runtime`、`GET /api/config/stage-models`、`PUT /api/config/stage-models`、`GET /api/system/info`。

#### Scenario: 获取配置
- **WHEN** CLI 请求 `GET /api/config`
- **THEN** 返回当前配置（隐藏敏感信息）

#### Scenario: 设置配置
- **WHEN** CLI 请求 `PUT /api/config/:key` with `{ value }`
- **THEN** 更新配置值

#### Scenario: 获取系统信息
- **WHEN** 请求 `GET /api/system/info`
- **THEN** 返回 `{ version, gitHash, server: { host, port, status }, paths: { db, config, opencode, logs } }`

#### Scenario: 获取 Mohist Model
- **WHEN** 请求 `GET /api/config/model`
- **THEN** 返回 `{ model: string | null }`

#### Scenario: 设置 Mohist Model
- **WHEN** 请求 `PUT /api/config/model` with `{ model: "openai/gpt-4o" }`
- **THEN** 更新 config.jsonc 中 `model` 字段

#### Scenario: 获取 Coder Model
- **WHEN** 请求 `GET /api/config/opencode-model`
- **THEN** 返回 `{ model: string | null }`

#### Scenario: 设置 Coder Model
- **WHEN** 请求 `PUT /api/config/opencode-model` with `{ model: "deepseek/deepseek-chat" }`
- **THEN** 更新 config.jsonc 中 `opencode.model` 字段

#### Scenario: 获取 Log Level
- **WHEN** 请求 `GET /api/config/log-level`
- **THEN** 返回 `{ level: string }`

#### Scenario: 设置 Log Level
- **WHEN** 请求 `PUT /api/config/log-level` with `{ level: "DEBUG" }`
- **THEN** 更新 config.jsonc 中 `log.level` 字段
- **AND** 运行时 logger 级别立即生效

#### Scenario: 批量更新 Agent Runtime 配置
- **WHEN** 请求 `PUT /api/config/agent-runtime` with `{ timeout, maxConcurrent, ... }`
- **THEN** 原子性更新所有提供的字段
- **AND** 验证失败时不更新任何字段

#### Scenario: 获取 Agent Runtime 配置
- **WHEN** 请求 `GET /api/config/agent-runtime`
- **THEN** 返回当前所有 agent runtime 配置值（含默认值）

#### Scenario: 获取 Stage Model Overrides
- **WHEN** 请求 `GET /api/config/stage-models`
- **THEN** 返回 `{ stageModels: Record<string, string> | null }`

#### Scenario: 设置 Stage Model Overrides
- **WHEN** 请求 `PUT /api/config/stage-models` with `{ stageModels: { build: "openai/gpt-4o" } }`
- **THEN** 更新 config.jsonc 中 `opencode.stageModels` 字段
