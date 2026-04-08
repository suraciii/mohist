## ADDED Requirements

### Requirement: 配置文件位置与格式

系统 SHALL 在 `~/.mohist/config.jsonc` 存储全局配置。文件格式为 JSONC（JSON with Comments），使用 `jsonc-parser` 解析。文件不存在时 SHALL 使用内置默认值，不报错。

#### Scenario: 配置文件不存在
- **WHEN** `~/.mohist/config.jsonc` 文件不存在
- **THEN** ConfigLoader SHALL 返回默认配置（无 provider、无 model 指定）
- **AND** 系统 SHALL 继续正常启动

#### Scenario: 配置文件包含注释
- **WHEN** config.jsonc 包含 `// line comment` 或 `/* block comment */`
- **THEN** ConfigLoader SHALL 正确解析，忽略注释

#### Scenario: 配置文件格式错误
- **WHEN** config.jsonc 包含无效 JSON 语法（在去除注释后）
- **THEN** ConfigLoader SHALL 抛出明确的解析错误，包含文件路径和错误位置
- **AND** Server 启动 SHALL 失败并打印错误信息

### Requirement: 配置文件 Zod Schema 校验

系统 SHALL 使用 Zod schema 校验 config.jsonc 的结构。Schema SHALL 定义以下顶层字段：

- `$schema`: 可选字符串
- `model`: 可选字符串，格式 "provider/model-id"
- `provider`: 可选 Record，键为 providerID，值为 ProviderConfig
- `server`: 可选对象，含 `port`
- `agent`: 可选对象，含 `timeout`、`maxConcurrent`

ProviderConfig SHALL 包含：
- `name`: 可选字符串，provider 显示名称
- `apiKey`: 可选字符串
- `baseURL`: 可选字符串
- `sdk`: 可选枚举 `"anthropic" | "openai" | "openai-compatible"`

未知字段 SHALL 被忽略（strip 语义），不报错。

#### Scenario: 完整配置校验通过
- **WHEN** config.jsonc 包含完整的 provider、model、server、agent 配置
- **THEN** Zod 校验 SHALL 通过
- **AND** ConfigLoader SHALL 返回完整的配置对象

#### Scenario: 空 JSON 对象
- **WHEN** config.jsonc 内容为 `{}`
- **THEN** Zod 校验 SHALL 通过
- **AND** ConfigLoader SHALL 返回全部为默认值的配置对象

#### Scenario: 无效字段类型
- **WHEN** config.jsonc 中 `model` 字段为数字 123
- **THEN** Zod 校验 SHALL 失败
- **AND** 错误信息 SHALL 明确指出字段路径和期望类型

### Requirement: 内置 Provider 注册表

系统 SHALL 内置以下 provider 定义，用户只需填 apiKey 即可使用：

| providerID | SDK 类型 | 默认 baseURL |
|---|---|---|
| `anthropic` | `anthropic` | (SDK 内置) |
| `openai` | `openai` | (SDK 内置) |
| `glm` | `openai-compatible` | `https://open.bigmodel.cn/api/paas/v4` |
| `kimi` | `openai-compatible` | `https://api.moonshot.cn/v1` |
| `minimax` | `openai-compatible` | `https://api.minimax.chat/v1` |
| `deepseek` | `openai-compatible` | `https://api.deepseek.com` |
| `qwen` | `openai-compatible` | `https://dashscope.aliyuncs.com/compatible-mode/v1` |

注册表 SHALL 为每个内置 provider 提供：`sdk` 类型、`name`（显示名称）、`baseURL`（可空）、`envVars`（对应环境变量名数组）。

#### Scenario: 查询内置 provider
- **WHEN** ConfigLoader 查询 providerID "glm"
- **THEN** SHALL 返回 `{ sdk: "openai-compatible", name: "智谱 GLM", baseURL: "https://open.bigmodel.cn/api/paas/v4", envVars: ["GLM_API_KEY"] }`

#### Scenario: 查询不存在的 provider
- **WHEN** ConfigLoader 查询 providerID "unknown-provider"
- **THEN** SHALL 返回 undefined 或 null
- **AND** 在 config.jsonc 中定义该 provider 时，自动识别为 `openai-compatible`

### Requirement: 配置合并优先级

系统 SHALL 按以下优先级合并 provider 配置（高优先级覆盖低优先级）：

1. config.jsonc 中的 `provider.<id>.apiKey`（最高）
2. 环境变量（如 `ANTHROPIC_API_KEY`、`GLM_API_KEY`）
3. 内置注册表的默认值（最低）

baseURL 合并优先级：
1. config.jsonc 中的 `provider.<id>.baseURL`
2. 内置注册表的默认 baseURL

#### Scenario: 配置文件 apiKey 覆盖环境变量
- **WHEN** config.jsonc 中 `provider.openai.apiKey` 为 "sk-file-key"
- **AND** 环境变量 `OPENAI_API_KEY` 为 "sk-env-key"
- **THEN** 系统 SHALL 使用 "sk-file-key"

#### Scenario: 环境变量 fallback
- **WHEN** config.jsonc 中未定义 `provider.openai`
- **AND** 环境变量 `OPENAI_API_KEY` 为 "sk-env-key"
- **THEN** 系统 SHALL 使用 "sk-env-key"

#### Scenario: 自定义 baseURL
- **WHEN** config.jsonc 中 `provider.openai.baseURL` 为 "https://proxy.example.com/v1"
- **THEN** 系统 SHALL 使用该 baseURL，覆盖 SDK 默认值

#### Scenario: 内置 provider 默认 baseURL
- **WHEN** config.jsonc 中 `provider.glm` 只有 `apiKey` 无 `baseURL`
- **THEN** 系统 SHALL 使用内置注册表中的 glm 默认 baseURL

### Requirement: 配置文件安全权限

系统 SHALL 在创建 `~/.mohist/config.jsonc` 时设置文件权限为 0600（仅 owner 可读写）。系统 SHALL 在创建 `~/.mohist/` 目录时设置权限为 0700。

#### Scenario: 首次创建配置文件
- **WHEN** ConfigLoader 首次写入 config.jsonc
- **THEN** 文件权限 SHALL 为 0600

#### Scenario: 目录权限
- **WHEN** `~/.mohist/` 目录由 ConfigLoader 创建
- **THEN** 目录权限 SHALL 为 0700

### Requirement: 配置文件读写原子性

系统 SHALL 在写入 config.jsonc 时使用写入临时文件 + rename 的方式，确保写入过程的原子性。

#### Scenario: 写入过程中崩溃
- **WHEN** ConfigLoader 正在写入 config.jsonc 时进程崩溃
- **THEN** 原有 config.jsonc SHALL 保持完整（不被截断或损坏）
