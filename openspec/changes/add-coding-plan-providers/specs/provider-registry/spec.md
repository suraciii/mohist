## ADDED Requirements

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
