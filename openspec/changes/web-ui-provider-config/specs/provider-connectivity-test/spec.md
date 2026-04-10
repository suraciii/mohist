## ADDED Requirements

### Requirement: 连接测试端点
系统 SHALL 提供统一 API 端点用于测试 Provider 连接，支持内置和自定义 Provider。

#### Scenario: 测试 Provider 连接
- **WHEN** 客户端发送 POST 请求到 `/api/providers/test`
- **AND** body 包含完整的 Provider 配置（id?, name, apiKey, baseURL, sdk, models?）
- **THEN** 系统使用提供的配置发送测试请求
- **AND** 返回测试结果，包含 success 状态和错误信息（如有）

#### Scenario: 测试内置 Provider（已保存配置）
- **GIVEN** Provider 配置已保存到 config.jsonc
- **WHEN** 客户端发送 POST 请求到 `/api/providers/test` 仅包含 id
- **THEN** 系统从 config.jsonc 加载完整配置并发送测试请求

#### Scenario: 连接测试成功
- **GIVEN** Provider 配置正确且网络可达
- **WHEN** 发送连接测试请求
- **THEN** 系统返回 `{ success: true }`，HTTP 200

#### Scenario: 连接测试失败（认证错误）
- **GIVEN** API Key 无效
- **WHEN** 发送连接测试请求
- **THEN** 系统返回 `{ success: false, error: "Authentication failed" }`，HTTP 400

#### Scenario: 连接测试失败（网络错误）
- **GIVEN** baseURL 不可达
- **WHEN** 发送连接测试请求
- **THEN** 系统返回 `{ success: false, error: "Connection timeout" }`，HTTP 400

### Requirement: 测试请求限流
系统 SHALL 对连接测试端点实施限流，防止滥用。

#### Scenario: 频繁测试被限流
- **GIVEN** 客户端在短时间内发送多次测试请求
- **WHEN** 超过限流阈值（每分钟 30 次）
- **THEN** 系统返回 429 Too Many Requests 错误

### Requirement: Web UI 连接测试
Web UI SHALL 在 Provider 配置对话框中提供测试连接按钮。

#### Scenario: 配置对话框测试连接
- **GIVEN** 用户在 Provider 配置对话框中输入了 API Key
- **WHEN** 用户点击 "Test Connection" 按钮
- **THEN** 系统发送测试请求，显示 loading 状态，然后显示测试结果（成功/失败）

#### Scenario: 测试按钮防抖
- **GIVEN** 用户频繁点击测试按钮
- **WHEN** 点击间隔小于 2 秒
- **THEN** 只有第一次点击触发请求，后续点击被忽略

### Requirement: 保存前强制测试
系统 SHOULD 在保存自定义 Provider 配置前执行连接测试，确保配置可用。

#### Scenario: 保存自定义 Provider 时测试失败
- **GIVEN** 用户填写自定义 Provider 表单
- **WHEN** 用户点击保存，但连接测试失败
- **THEN** 系统显示警告，询问是否仍要保存（可选强制保存）
