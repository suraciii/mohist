## MODIFIED Requirements

### Requirement: Provider 配置保存
系统 SHALL 允许通过 API 保存或更新 Provider 配置，配置 SHALL 写入 `~/.mohist/config.jsonc`。系统 SHALL 确保 API Key 在日志和错误消息中被正确 mask。

#### Scenario: 配置内置 Provider
- **WHEN** 客户端发送 POST 请求到 `/api/providers/:id`，body 包含 apiKey
- **THEN** 系统验证 apiKey 非空，更新 config.jsonc，返回 success
- **AND** 任何日志输出中的 apiKey SHALL 被 mask 处理

#### Scenario: 配置验证失败
- **WHEN** 客户端发送 POST 请求包含无效数据（如空 apiKey、无效 baseURL）
- **THEN** 系统返回 400 错误，包含具体验证错误信息
- **AND** 错误信息中 SHALL NOT 包含完整的 apiKey

#### Scenario: API Key 在日志中被 mask
- **GIVEN** 系统正在记录包含 Provider 配置的对象
- **WHEN** 对象包含 apiKey 字段
- **THEN** 日志输出 SHALL 显示 mask 后的 apiKey（如 `sk-ab*********789`）

## ADDED Requirements

### Requirement: API Key 安全日志
系统 SHALL 提供工具函数确保敏感信息不会泄露到日志中。

#### Scenario: Mask 工具函数处理对象
- **GIVEN** 一个包含 apiKey、secret、token 字段的对象
- **WHEN** maskSensitiveData() 函数被调用
- **THEN** 返回值 SHALL 包含被 mask 后的敏感字段

#### Scenario: Mask 工具函数处理嵌套对象
- **GIVEN** 一个嵌套对象，深层包含 apiKey 字段
- **WHEN** maskSensitiveData() 函数被调用
- **THEN** 所有层级的敏感字段 SHALL 被 mask
