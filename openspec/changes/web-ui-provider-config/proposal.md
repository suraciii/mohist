## Why

当前 Mohist 仅支持通过 CLI 命令 `mo providers login` 配置 LLM Provider，Web UI 无法完成此操作。用户需要在终端和浏览器之间切换，体验割裂。同时，配置变更后需要重启 Server 才能生效，且无法验证 Provider 连接是否成功，增加了配置调试成本。

## What Changes

- **新增 Web UI Provider 配置页面**: 在 Web UI 设置页面中新增 Providers Tab，支持查看、添加、删除 Provider 配置
- **支持自定义 Provider**: Web UI 支持配置自定义 OpenAI-compatible Provider（输入 baseURL、API Key、模型列表等）
- **配置热重载**: Provider 配置变更后无需重启 Server，实时生效
- **连接测试功能**: 配置 Provider 时可发送测试请求验证连接和认证是否成功
- **API 扩展**: 新增 `/api/providers` 相关端点支持 Web UI 操作

## Capabilities

### New Capabilities
- `provider-management`: Provider 管理功能，包括列表查看、添加、删除、配置更新
- `provider-hot-reload`: Provider 配置热重载机制，配置变更后自动应用到运行时的 Agent Runner
- `provider-connectivity-test`: Provider 连接测试功能，验证 API Key 和网络连通性

### Modified Capabilities
- `web-ui-settings`: 扩展设置页面，新增 Providers 配置 Tab

## Impact

- **Frontend**: 新增 SettingsProviders 组件、Provider 连接对话框、自定义 Provider 表单
- **Backend API**: 新增 GET/POST/DELETE `/api/providers` 端点
- **Config Layer**: ConfigService 需要支持热重载和变更通知机制
- **Agent Runner**: AgentRunnerService 需要监听配置变更，动态重新初始化 LLM Client
- **Storage**: 继续使用 `~/.mohist/config.jsonc`，与 CLI 配置保持兼容
