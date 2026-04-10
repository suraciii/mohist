## Why

在对 web-ui-provider-config 功能进行深入审查后，发现该功能虽然核心实现质量较高，但存在测试覆盖不足、潜在内存泄漏风险、安全隐患以及配置冲突处理缺失等问题。这些改进将提升系统的健壮性、安全性和可维护性。

## What Changes

- **新增 Provider API 测试套件**: 为 `/api/providers` 端点（GET、POST、DELETE、POST /test）添加完整的单元测试和集成测试
- **封装 RateLimiter 类**: 将限流器从全局模块级 Map 重构为可管理的类，支持生命周期管理，防止内存泄漏
- **增强 API Key 安全性**: 审查并加固所有日志输出，确保 API Key 不会泄露到日志中
- **实现配置版本冲突检测**: 添加乐观锁机制，防止 CLI 和 Web UI 同时修改配置时的覆盖问题
- **添加前端组件测试**: 为 SettingsPage、ProviderConnectDialog、CustomProviderDialog 添加 React Testing Library 测试

## Capabilities

### New Capabilities

- `provider-api-testing`: Provider API 测试套件，包括端点测试、热重载测试、限流测试
- `rate-limiter-service`: 限流器服务，支持生命周期管理和内存清理
- `config-version-control`: 配置版本控制，支持乐观锁和冲突检测
- `frontend-component-testing`: 前端组件测试基础设施

### Modified Capabilities

- `provider-management`: 扩展安全要求，要求 API Key 在日志中被正确 mask
- `provider-hot-reload`: 增强 EventBus 生命周期管理，避免服务关闭时影响其他服务

## Impact

- **Backend**: 新增测试文件、重构限流器、添加配置版本控制
- **Frontend**: 新增测试文件、测试基础设施配置
- **Dependencies**: 可能新增 vitest/@testing-library 等测试依赖
- **Storage**: 配置文件可能新增 `_version` 字段用于版本控制
- **API Behavior**: 配置保存接口可能返回 409 Conflict 当检测到版本冲突
