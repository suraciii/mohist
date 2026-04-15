## MODIFIED Requirements

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
| `zhipuai-coding-plan` | `openai-compatible` | `https://open.bigmodel.cn/api/coding/paas/v4` |
| `kimi-for-coding` | `anthropic` | `https://api.kimi.com/coding/v1` |
| `minimax-for-coding` | `anthropic` | `https://api.minimax.io/anthropic/v1` |

注册表 SHALL 为每个内置 provider 提供：`sdk` 类型、`name`（显示名称）、`baseURL`（可空）、`envVars`（对应环境变量名数组）。

#### Scenario: 查询内置 provider
- **WHEN** ConfigLoader 查询 providerID "glm"
- **THEN** SHALL 返回 `{ sdk: "openai-compatible", name: "智谱 GLM", baseURL: "https://open.bigmodel.cn/api/paas/v4", envVars: ["GLM_API_KEY"] }`

#### Scenario: 查询不存在的 provider
- **WHEN** ConfigLoader 查询 providerID "unknown-provider"
- **THEN** SHALL 返回 undefined 或 null
- **AND** 在 config.jsonc 中定义该 provider 时，自动识别为 `openai-compatible`

#### Scenario: 查询 zhipuai-coding-plan provider
- **WHEN** ConfigLoader 查询 providerID "zhipuai-coding-plan"
- **THEN** SHALL 返回 `{ sdk: "openai-compatible", name: "智谱 Coding Plan", baseURL: "https://open.bigmodel.cn/api/coding/paas/v4", envVars: ["ZHIPU_API_KEY"] }`

#### Scenario: 查询 kimi-for-coding provider
- **WHEN** ConfigLoader 查询 providerID "kimi-for-coding"
- **THEN** SHALL 返回 `{ sdk: "anthropic", name: "Kimi For Coding", baseURL: "https://api.kimi.com/coding/v1", envVars: ["KIMI_API_KEY", "MOONSHOT_API_KEY"] }`

#### Scenario: 查询 minimax-for-coding provider
- **WHEN** ConfigLoader 查询 providerID "minimax-for-coding"
- **THEN** SHALL 返回 `{ sdk: "anthropic", name: "MiniMax Coding", baseURL: "https://api.minimax.io/anthropic/v1", envVars: ["MINIMAX_API_KEY"] }`
