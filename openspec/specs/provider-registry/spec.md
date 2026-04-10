## Requirements

### Requirement: 动态 provider 解析

系统 SHALL 根据注册表中的 SDK 类型动态创建 LLM provider 实例，不再使用硬编码 switch-case。支持的 SDK 类型：

- `anthropic`: 使用 `createAnthropic({ apiKey })` 创建
- `openai`: 使用 `createOpenAI({ apiKey })` 创建
- `openai-compatible`: 使用 `createOpenAI({ apiKey, baseURL })` 创建

未在注册表中定义但出现在 config.jsonc `provider` 段的 providerID SHALL 自动作为 `openai-compatible` 处理。

#### Scenario: 使用内置 anthropic provider
- **WHEN** 用户配置 `model: "anthropic/claude-sonnet-4-20250514"`
- **AND** `provider.anthropic.apiKey` 已设置
- **THEN** resolveModel() SHALL 使用 `createAnthropic({ apiKey })` 创建实例
- **AND** 调用 `sdk("claude-sonnet-4-20250514")` 获取 LanguageModelV3

#### Scenario: 使用内置 openai provider
- **WHEN** 用户配置 `model: "openai/gpt-4o"`
- **AND** `provider.openai.apiKey` 已设置
- **THEN** resolveModel() SHALL 使用 `createOpenAI({ apiKey })` 创建实例

#### Scenario: 使用内置国产 provider (openai-compatible)
- **WHEN** 用户配置 `model: "glm/glm-4-plus"`
- **AND** `provider.glm.apiKey` 已设置
- **THEN** resolveModel() SHALL 使用 `createOpenAI({ apiKey, baseURL: "https://open.bigmodel.cn/api/paas/v4" })` 创建实例
- **AND** 调用 `sdk("glm-4-plus")` 获取 LanguageModelV3

#### Scenario: 使用自定义 provider
- **WHEN** 用户配置 `provider.my-llm` 和 `model: "my-llm/some-model"`
- **AND** `provider.my-llm` 包含 `baseURL` 和 `apiKey`
- **THEN** resolveModel() SHALL 自动识别为 `openai-compatible`
- **AND** 使用 `createOpenAI({ apiKey, baseURL })` 创建实例

#### Scenario: 自定义 provider 覆盖 SDK 类型
- **WHEN** 用户配置 `provider.my-llm` 包含 `sdk: "anthropic"`
- **AND** `provider.my-llm` 包含 `baseURL` 和 `apiKey`
- **THEN** resolveModel() SHALL 使用 `createAnthropic({ apiKey, baseURL })` 创建实例

### Requirement: Model ID 解析

系统 SHALL 将 model 字符串按第一个 `/` 分割为 providerID 和 modelID。格式为 `"provider/model-id"`。

#### Scenario: 标准格式
- **WHEN** model 字符串为 "glm/glm-4-plus"
- **THEN** providerID SHALL 为 "glm"，modelID SHALL 为 "glm-4-plus"

#### Scenario: 无效格式
- **WHEN** model 字符串为 "just-a-string"（无斜杠）
- **THEN** resolveModel() SHALL 抛出错误，提示期望 "provider/model-id" 格式

#### Scenario: 空 provider 或空 model
- **WHEN** model 字符串为 "/model" 或 "provider/"
- **THEN** resolveModel() SHALL 抛出错误

### Requirement: API key 缺失时报错

当 resolved provider 没有 API key（配置文件和环境变量均无）时，resolveModel() SHALL 抛出明确的错误信息，包含：providerID、对应的环境变量名、配置文件路径。

#### Scenario: API key 缺失
- **WHEN** 用户配置 `model: "anthropic/claude-sonnet-4-20250514"`
- **AND** config.jsonc 中无 `provider.anthropic.apiKey`
- **AND** 环境变量 `ANTHROPIC_API_KEY` 未设置
- **THEN** resolveModel() SHALL 抛出错误: `API key not found for provider "anthropic". Set provider.anthropic.apiKey in ~/.mohist/config.jsonc or set ANTHROPIC_API_KEY environment variable.`

### Requirement: 默认模型

当 config.jsonc 中未设置 `model` 字段时，系统 SHALL 使用 `"anthropic/claude-sonnet-4-20250514"` 作为默认模型。

#### Scenario: 无 model 配置
- **WHEN** config.jsonc 不存在或无 `model` 字段
- **THEN** resolveModel() SHALL 使用 "anthropic/claude-sonnet-4-20250514"

### Requirement: Coding Plan Provider 动态解析

系统 SHALL 支持 coding plan provider 的 SDK 实例创建，其中 `kimi-for-coding` 和 `minimax-for-coding` 使用 `anthropic` SDK，`zhipuai-coding-plan` 使用 `openai-compatible` SDK。

#### Scenario: 使用 kimi-for-coding provider
- **WHEN** 用户配置 `model: "kimi-for-coding/k2p5"`
- **AND** 环境变量 `KIMI_API_KEY` 已设置
- **THEN** resolveModel() SHALL 使用 `createAnthropic({ apiKey, baseURL: "https://api.kimi.com/coding/v1" })` 创建实例
- **AND** 调用 `sdk("k2p5")` 获取 LanguageModelV3

#### Scenario: 使用 minimax-for-coding provider
- **WHEN** 用户配置 `model: "minimax-for-coding/MiniMax-M2.5"`
- **AND** 环境变量 `MINIMAX_API_KEY` 已设置
- **THEN** resolveModel() SHALL 使用 `createAnthropic({ apiKey, baseURL: "https://api.minimax.io/anthropic/v1" })` 创建实例

#### Scenario: 使用 zhipuai-coding-plan provider
- **WHEN** 用户配置 `model: "zhipuai-coding-plan/glm-5.1"`
- **AND** 环境变量 `ZHIPU_API_KEY` 已设置
- **THEN** resolveModel() SHALL 使用 `createOpenAI({ apiKey, baseURL: "https://open.bigmodel.cn/api/coding/paas/v4" })` 创建实例
