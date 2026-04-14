## MODIFIED Requirements

### Requirement: LLM config is loaded from config.jsonc and passed to resolveModel

系统 SHALL 从 `~/.mohist/config.jsonc` 读取 LLM 配置并传给 `resolveModel()`。当 config.jsonc 中无 `model` 字段时，SHALL 使用智能默认模型选择逻辑（见 smart-default-model spec），不再使用硬编码的 anthropic 默认模型。

#### Scenario: LLM model configured in config.jsonc
- **WHEN** `model` is set to "minimax/MiniMax-M2.7" in config.jsonc
- **THEN** `resolveModel()` SHALL use that model

#### Scenario: No LLM config in config.jsonc
- **WHEN** config.jsonc does not exist or has no `model` field
- **AND** `provider.minimax.apiKey` is configured
- **THEN** `resolveModel()` SHALL auto-select the latest model from minimax

#### Scenario: No provider configured at all
- **WHEN** config.jsonc does not exist or has no `model` field
- **AND** no provider has an apiKey configured
- **AND** no relevant environment variables are set
- **THEN** `resolveModel()` SHALL throw a user-friendly error explaining how to configure a provider

### Requirement: LLM provider configuration

系统 SHALL 支持通过 `~/.mohist/config.jsonc` 配置 LLM provider。Provider 注册表 SHALL 从 models.dev 数据动态生成，支持用户覆盖。配置合并优先级不变：config.jsonc apiKey > 环境变量 > 内置默认值。

#### Scenario: Load provider config from config.jsonc
- **WHEN** Mohist server starts
- **THEN** the system SHALL load config.jsonc via ConfigLoader
- **THEN** the system SHALL detect API key from config.jsonc or environment variables
- **THEN** the configured or auto-selected model SHALL be used for LLM calls
