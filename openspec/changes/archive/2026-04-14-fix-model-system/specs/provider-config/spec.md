## MODIFIED Requirements

### Requirement: 内置 Provider 注册表

系统 SHALL 从 models.dev 数据动态生成 provider 注册表，且 **直接使用 models.dev 的 provider ID**（如 `minimax`、`moonshotai`、`zhipuai`、`alibaba`）。对于 models.dev 中包含的 provider，系统 SHALL 使用 models.dev 提供的 `npm`、`api`、`env` 字段，不再手动维护 SDK 类型、baseURL 和环境变量映射。

对于 models.dev 中未包含的 provider，系统 SHALL 保留静态 fallback 定义。

用户在 config.jsonc 中通过 `provider.models` 字段覆盖模型列表的能力保持不变。

#### Scenario: 从 models.dev 生成 minimax provider
- **WHEN** models.dev 数据中包含 provider "minimax"，npm 为 `@ai-sdk/anthropic`，api 为 `https://api.minimax.io/anthropic/v1`，env 为 `["MINIMAX_API_KEY"]`
- **THEN** 系统 SHALL 生成 provider 注册条目 `{ sdk: "anthropic", name: "MiniMax (minimax.io)", baseURL: "https://api.minimax.io/anthropic/v1", envVars: ["MINIMAX_API_KEY"] }`

#### Scenario: models.dev 中不存在的 provider 使用 fallback
- **WHEN** models.dev 数据中不包含某个自定义 provider
- **THEN** 系统 SHALL 使用静态 fallback 定义，或根据用户在 config.jsonc 中提供的 `baseURL`、`sdk` 自动识别

#### Scenario: 用户在 config 中覆盖 provider 模型列表
- **WHEN** config.jsonc 中 `provider.minimax.models` 设置为 `["MiniMax-M2.7"]`
- **THEN** 系统 SHALL 使用用户指定的模型列表，而非 models.dev 的完整列表

#### Scenario: 用户在 config 中自定义 provider
- **WHEN** config.jsonc 中定义了 `provider.my-llm` 且不在 models.dev 中
- **AND** 包含 `apiKey`、`baseURL`、`models`
- **THEN** 系统 SHALL 自动识别为 `openai-compatible` SDK 类型
