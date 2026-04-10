## ADDED Requirements

### Requirement: Provider 列表查询
系统 SHALL 提供 API 端点返回所有可用的 Provider 列表，包括内置 Provider 和用户已配置的自定义 Provider。

#### Scenario: 获取 Provider 列表
- **WHEN** 客户端发送 GET 请求到 `/api/providers`
- **THEN** 系统返回包含所有 Provider 的列表，每个 Provider 包含 id、name、baseURL、configured 状态、source（env/config/none）

### Requirement: Provider 配置保存
系统 SHALL 允许通过 API 保存或更新 Provider 配置，配置 SHALL 写入 `~/.mohist/config.jsonc`。

#### Scenario: 配置内置 Provider
- **WHEN** 客户端发送 POST 请求到 `/api/providers/:id`，body 包含 apiKey
- **THEN** 系统验证 apiKey 非空，更新 config.jsonc，返回 success

#### Scenario: 配置自定义 Provider
- **WHEN** 客户端发送 POST 请求到 `/api/providers/:id`，body 包含 apiKey、baseURL、models、name
- **THEN** 系统验证 id 格式（a-z, 0-9, -）、baseURL 格式、models 非空，更新 config.jsonc

#### Scenario: 配置验证失败
- **WHEN** 客户端发送 POST 请求包含无效数据（如空 apiKey、无效 baseURL）
- **THEN** 系统返回 400 错误，包含具体验证错误信息

### Requirement: Provider 配置删除
系统 SHALL 允许通过 API 删除 Provider 配置，从 `~/.mohist/config.jsonc` 中移除对应条目。

#### Scenario: 删除 Provider 配置
- **WHEN** 客户端发送 DELETE 请求到 `/api/providers/:id`
- **THEN** 系统从 config.jsonc 中移除该 Provider 配置，返回 success

### Requirement: Web UI Provider 设置页面
Web UI SHALL 提供设置页面供用户管理 Provider 配置。

#### Scenario: 显示已连接 Provider
- **WHEN** 用户访问设置页面的 Providers Tab
- **THEN** 页面显示已配置的 Provider 列表，显示 mask 后的 API Key 和来源标识

#### Scenario: 显示可用 Provider
- **WHEN** 用户访问设置页面的 Providers Tab
- **THEN** 页面显示所有内置但未配置的 Provider，提供 Connect 按钮

#### Scenario: 连接 Provider
- **WHEN** 用户点击内置 Provider 的 Connect 按钮
- **THEN** 弹出对话框，提示输入 API Key，支持测试连接和保存

#### Scenario: 自定义 Provider
- **WHEN** 用户点击 Custom Provider 按钮
- **THEN** 弹出表单，支持输入 Provider ID、名称、Base URL、API Key、模型列表
