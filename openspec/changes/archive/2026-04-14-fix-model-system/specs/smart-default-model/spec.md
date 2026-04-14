## ADDED Requirements

### Requirement: 自动选择默认模型

当 config.jsonc 中未设置 `model` 字段时，系统 SHALL 自动从已配置（有 apiKey）的 provider 中选择最新模型作为默认模型。选择算法：遍历所有有 apiKey 的 provider，从 models.dev 数据中取每个 provider 的模型列表，按 `release_date` 降序排列，返回第一个 `provider/model-id`。

#### Scenario: 有一个已配置 provider
- **WHEN** config.jsonc 中 `provider.minimax.apiKey` 已设置
- **AND** config.jsonc 中无 `model` 字段
- **AND** models.dev 数据中 minimax 的最新模型是 `MiniMax-M2.7`
- **THEN** resolveModel() SHALL 使用 `"minimax/MiniMax-M2.7"` 作为模型

#### Scenario: 有多个已配置 provider
- **WHEN** config.jsonc 中 `provider.minimax.apiKey` 和 `provider.zhipuai-coding-plan.apiKey` 均已设置
- **AND** config.jsonc 中无 `model` 字段
- **AND** minimax 最新模型 release_date 为 2026-03-18
- **AND** zhipuai-coding-plan 最新模型 release_date 为 2026-02-11
- **THEN** resolveModel() SHALL 选择 release_date 最新的模型 `"minimax/MiniMax-M2.7"`

#### Scenario: 无已配置 provider
- **WHEN** config.jsonc 中无任何 provider 配置了 apiKey
- **AND** 环境变量中也没有任何 provider 对应的 API key
- **THEN** resolveModel() SHALL 抛出友好错误："No LLM provider configured. Configure a provider in config or set an API key environment variable."

#### Scenario: config.jsonc 显式指定 model
- **WHEN** config.jsonc 中 `model` 字段为 `"minimax/MiniMax-M2.5"`
- **THEN** resolveModel() SHALL 使用显式指定的模型，忽略自动选择逻辑

### Requirement: 默认模型选择不考虑未配置 provider

只有 apiKey 来源为 `config` 或 `env` 的 provider SHALL 参与默认模型选择。apiKey 为 null（来源 `none`）的 provider SHALL 被跳过。

#### Scenario: 跳过未配置 provider
- **WHEN** provider "anthropic" 存在于 models.dev 数据中但未配置 apiKey
- **AND** provider "minimax" 已配置 apiKey
- **THEN** 只有 minimax 的模型 SHALL 参与默认选择
- **AND** anthropic 的模型 SHALL 被跳过
