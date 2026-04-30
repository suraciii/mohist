## ADDED Requirements

### Requirement: System Info API 端点

Server SHALL 提供 `GET /api/system/info` 端点，返回系统运行时信息，包括版本、git hash、server 状态、各关键路径。

#### Scenario: 获取系统信息
- **WHEN** 请求 `GET /api/system/info`
- **THEN** 返回 JSON 对象包含以下字段：
  - `version`: string — Mohist 版本号（从 package.json 读取）
  - `gitHash`: string — 当前 git commit hash（短格式，7 字符），无法获取时为 "unknown"
  - `server`: `{ host: string, port: number, status: "running" }`
  - `paths`: `{ db: string, config: string, opencode: string | null, logs: string }`
  - `paths.db` 为 `~/.mohist/mohist.db` 的绝对路径
  - `paths.config` 为 `~/.mohist/config.jsonc` 的绝对路径
  - `paths.opencode` 为 opencode 二进制路径（通过 `which opencode` 或配置获取），未找到时为 null（类型 `string | null`）
  - `paths.logs` 为 `~/.mohist/logs/` 的绝对路径

#### Scenario: opencode 二进制未找到
- **WHEN** 系统中未安装 opencode
- **THEN** `paths.opencode` 为 null
- **AND** API 仍返回 200

#### Scenario: git 信息不可用
- **WHEN** 应用未在 git 仓库中运行
- **THEN** `gitHash` 为 "unknown"
- **AND** API 仍返回 200

### Requirement: Mohist Model 配置 API

Server SHALL 提供 `GET /api/config/model` 和 `PUT /api/config/model` 端点，用于读写 `config.model`（Mohist 使用的默认模型）。

#### Scenario: 获取当前 model
- **WHEN** 请求 `GET /api/config/model`
- **THEN** 返回 `{ model: string | null }`
- **AND** `model` 为 config.jsonc 中的 `model` 字段值

#### Scenario: 设置 model
- **WHEN** 请求 `PUT /api/config/model` with `{ model: "openai/gpt-4o" }`
- **THEN** 更新 config.jsonc 中的 `model` 字段为 "openai/gpt-4o"
- **AND** 返回 `{ model: "openai/gpt-4o" }`

#### Scenario: 设置无效 model 格式
- **WHEN** 请求 `PUT /api/config/model` with `{ model: "invalid-no-slash" }`
- **THEN** 返回 400 错误
- **AND** 错误信息包含 "Invalid model format"

#### Scenario: 清除 model
- **WHEN** 请求 `PUT /api/config/model` with `{ model: null }`
- **THEN** 删除 config.jsonc 中的 `model` 字段
- **AND** 返回 `{ model: null }`

### Requirement: Coder Model 配置 API

Server SHALL 提供 `GET /api/config/opencode-model` 和 `PUT /api/config/opencode-model` 端点，用于读写 `config.opencode.model`（coder agent 使用的模型）。

#### Scenario: 获取 coder model
- **WHEN** 请求 `GET /api/config/opencode-model`
- **THEN** 返回 `{ model: string | null }`
- **AND** `model` 为 config.jsonc 中的 `opencode.model` 字段值

#### Scenario: 设置 coder model
- **WHEN** 请求 `PUT /api/config/opencode-model` with `{ model: "deepseek/deepseek-chat" }`
- **THEN** 更新 config.jsonc 中的 `opencode.model` 字段
- **AND** 返回 `{ model: "deepseek/deepseek-chat" }`

#### Scenario: 清除 coder model
- **WHEN** 请求 `PUT /api/config/opencode-model` with `{ model: null }`
- **THEN** 删除 config.jsonc 中的 `opencode.model` 字段
- **AND** 返回 `{ model: null }`

### Requirement: Log Level 配置 API

Server SHALL 提供 `GET /api/config/log-level` 和 `PUT /api/config/log-level` 端点，用于读写 `config.log.level`。

#### Scenario: 获取 log level
- **WHEN** 请求 `GET /api/config/log-level`
- **THEN** 返回 `{ level: string }`
- **AND** `level` 为 config.jsonc 中的 `log.level` 字段值，未配置时为 "INFO"

#### Scenario: 设置 log level
- **WHEN** 请求 `PUT /api/config/log-level` with `{ level: "DEBUG" }`
- **THEN** 更新 config.jsonc 中的 `log.level` 字段为 "DEBUG"
- **AND** 运行时 logger 级别立即生效（不重启）
- **AND** 返回 `{ level: "DEBUG" }`

#### Scenario: 设置无效 log level
- **WHEN** 请求 `PUT /api/config/log-level` with `{ level: "VERBOSE" }`
- **THEN** 返回 400 错误
- **AND** 错误信息包含 "Invalid log level"

### Requirement: Agent Runtime 批量配置 API

Server SHALL 提供 `GET /api/config/agent-runtime` 和 `PUT /api/config/agent-runtime` 端点。GET 返回当前所有 agent runtime 配置值，PUT 支持一次性批量更新。

#### Scenario: 获取 agent runtime 配置
- **WHEN** 请求 `GET /api/config/agent-runtime`
- **THEN** 返回当前配置对象，包含 `timeout`、`stageTimeout`、`taskTimeout`、`maxConcurrent`、`maxGracePeriods`、`pollInterval` 字段
- **AND** 未配置的字段返回对应默认值（timeout: 1800000, stageTimeout: 3600000, taskTimeout: 600000, maxConcurrent: 8, maxGracePeriods: 2, pollInterval: 30000）

#### Scenario: 批量更新 agent runtime 配置
- **WHEN** 请求 `PUT /api/config/agent-runtime` with `{ timeout: 2700000, stageTimeout: 3600000, taskTimeout: 600000, maxConcurrent: 4, maxGracePeriods: 3, pollInterval: 15000 }`
- **THEN** 更新 config.jsonc 中所有提供的字段
- **AND** 未提供的字段保持不变
- **AND** 返回更新后的完整配置对象

#### Scenario: 部分更新
- **WHEN** 请求 `PUT /api/config/agent-runtime` with `{ timeout: 3600000, maxConcurrent: 2 }`
- **THEN** 只更新 `agent.timeout` 和 `agent.maxConcurrent`
- **AND** 其他字段保持不变

#### Scenario: 验证所有字段
- **WHEN** 请求中 `maxConcurrent` 为 -1
- **THEN** 返回 400 错误
- **AND** 不更新任何字段（原子操作）

### Requirement: Stage Model Overrides 配置 API

Server SHALL 提供 `GET /api/config/stage-models` 和 `PUT /api/config/stage-models` 端点，用于读写 `config.opencode.stageModels`（各 stage 的模型覆盖）。

#### Scenario: 获取 stage model overrides
- **WHEN** 请求 `GET /api/config/stage-models`
- **THEN** 返回 `{ stageModels: Record<string, string> | null }`
- **AND** `stageModels` 为 config.jsonc 中 `opencode.stageModels` 的值，未配置时为 null

#### Scenario: 设置 stage model override
- **WHEN** 请求 `PUT /api/config/stage-models` with `{ stageModels: { build: "openai/gpt-4o" } }`
- **THEN** 更新 config.jsonc 中 `opencode.stageModels` 为 `{ build: "openai/gpt-4o" }`
- **AND** 返回 `{ stageModels: { build: "openai/gpt-4o" } }`

#### Scenario: 清除所有 stage model overrides
- **WHEN** 请求 `PUT /api/config/stage-models` with `{ stageModels: null }`
- **THEN** 删除 config.jsonc 中的 `opencode.stageModels` 字段
- **AND** 返回 `{ stageModels: null }`

#### Scenario: 验证 model 格式
- **WHEN** 请求中某个 stage 的 model 值不含 `/`
- **THEN** 返回 400 错误
- **AND** 错误信息包含 "Invalid model format"
