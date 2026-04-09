## ADDED Requirements

### Requirement: LLM 错误分类为结构化错误码

系统 SHALL 定义 `LlmError` 类（继承 `Error`），携带 `code` 字段用于错误分类。`resolveModel()` 在找不到 API key 时 SHALL 抛出 `LlmError` 而非普通 `Error`。

错误码定义：
- `LLM_NOT_CONFIGURED` — provider 未配置或 apiKey 缺失
- `LLM_CONFIG_INVALID` — config.jsonc 存在但格式错误或无法解析
- `LLM_AUTH_FAILED` — provider 返回认证失败（401/403），供后续扩展
- `LLM_RATE_LIMITED` — provider 返回限流（429），供后续扩展
- `LLM_PROVIDER_ERROR` — provider 服务端错误（5xx），供后续扩展

当前只实现 `LLM_NOT_CONFIGURED` 和 `LLM_CONFIG_INVALID`，其他错误码为预留。

#### Scenario: resolveModel 无 API key 时抛出 LlmError
- **WHEN** `resolveModel()` 被调用
- **AND** 对应 provider 的 apiKey 不存在
- **THEN** SHALL 抛出 `LlmError` 实例
- **AND** `error.code` 为 `"LLM_NOT_CONFIGURED"`
- **AND** `error.message` 包含可读的错误描述

#### Scenario: config.jsonc 格式错误时抛出 LlmError
- **WHEN** `~/.mohist/config.jsonc` 存在但 JSONC 语法错误
- **AND** `resolveModel()` 被调用
- **THEN** SHALL 抛出 `LlmError` 实例
- **AND** `error.code` 为 `"LLM_CONFIG_INVALID"`
- **AND** `error.message` 包含配置文件路径和语法错误信息

#### Scenario: resolveModel 配置正确时正常返回
- **WHEN** `resolveModel()` 被调用
- **AND** `~/.mohist/config.jsonc` 格式正确且对应 provider 的 apiKey 已配置
- **THEN** SHALL 正常返回 `LanguageModelV3` 实例，不抛错

#### Scenario: 非 LLM 错误不受影响
- **WHEN** 其他代码路径抛出普通 `Error`
- **THEN** 该错误 SHALL NOT 包含 `code` 字段
- **AND** API 层按原有逻辑处理
