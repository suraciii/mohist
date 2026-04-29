## ADDED Requirements

### Requirement: 读取默认 coder model 配置

系统 SHALL 提供 `GET /api/opencode-config/model` 端点，读取 `~/.mohist/config.jsonc` 中 `opencode.model` 字段的值并返回。

#### Scenario: opencode.model 已配置

- **WHEN** `config.jsonc` 中 `opencode.model` 为 `"anthropic/claude-sonnet-4"`
- **THEN** 返回 `{ success: true, data: { model: "anthropic/claude-sonnet-4" } }`

#### Scenario: opencode.model 未配置

- **WHEN** `config.jsonc` 中不存在 `opencode` 或 `opencode.model` 字段
- **THEN** 返回 `{ success: true, data: { model: null } }`

#### Scenario: config.jsonc 文件不存在

- **WHEN** `~/.mohist/config.jsonc` 文件不存在
- **THEN** 返回 `{ success: true, data: { model: null } }`

### Requirement: 写入默认 coder model 配置

系统 SHALL 提供 `PUT /api/opencode-config/model` 端点，通过 `load()` → 修改 `opencode.model` → `writeConfig()` 写入 `config.jsonc`。

#### Scenario: 设置 opencode.model

- **WHEN** 请求 body 为 `{ model: "anthropic/claude-sonnet-4" }`
- **THEN** 系统 SHALL 读取当前 config，设置 `opencode.model` 为 `"anthropic/claude-sonnet-4"`，写回 config.jsonc
- **AND** 返回 `{ success: true, data: { model: "anthropic/claude-sonnet-4" } }`

#### Scenario: 清除 opencode.model（恢复默认）

- **WHEN** 请求 body 为 `{ model: null }`
- **THEN** 系统 SHALL 读取当前 config，删除 `opencode.model` 字段（或设为 undefined），写回 config.jsonc
- **AND** 返回 `{ success: true, data: { model: null } }`

#### Scenario: 请求体缺少 model 字段

- **WHEN** 请求 body 不包含 `model` 字段
- **THEN** 返回 `{ success: false, error: "model is required" }`，HTTP 400

#### Scenario: model 值类型错误

- **WHEN** 请求 body 中 `model` 为数字 `123`
- **THEN** 返回 `{ success: false, error: "model must be a string or null" }`，HTTP 400

#### Scenario: 写入冲突（乐观锁）

- **WHEN** config.jsonc 在读取后被外部修改（版本号不匹配）
- **THEN** 返回 `{ success: false, error: "Config was modified by another process" }`，HTTP 409

### Requirement: opencode-model API 使用 config-loader 读写

系统 SHALL 使用 `config-loader` 的 `load()` 和 `writeConfig()` 函数读写 `config.jsonc`，与 provider 配置使用相同的读写路径，不使用 SQLite ConfigService。

#### Scenario: 写入后 config cache 被清除

- **WHEN** `PUT /api/config/opencode-model` 成功写入
- **THEN** `config-loader` 的内存缓存 SHALL 被清除，后续 `load()` 调用读取最新文件内容
