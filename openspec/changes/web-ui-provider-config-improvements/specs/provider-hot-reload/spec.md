## MODIFIED Requirements

### Requirement: AgentRunner 热重载
AgentRunnerService SHALL 监听 Provider 配置变更事件，动态重新初始化 LLM Provider。AgentRunnerService SHALL 正确管理事件监听器生命周期，避免服务关闭时影响其他服务。

#### Scenario: 配置变更后自动重载
- **GIVEN** AgentRunnerService 正在运行
- **WHEN** 收到 `config:providers:changed` 事件
- **THEN** AgentRunnerService 使用新配置重新初始化 LLM Client

#### Scenario: AgentRunner 关闭时清理监听器
- **GIVEN** AgentRunnerService 已注册事件监听器
- **WHEN** AgentRunnerService.shutdown() 被调用
- **THEN** 系统 SHALL 只移除当前服务注册的监听器，不影响其他服务
- **AND** 系统 SHALL NOT 调用 removeAllListeners() 清除所有监听器

#### Scenario: 多个 AgentRunner 实例共存
- **GIVEN** 多个 AgentRunnerService 实例在运行
- **WHEN** 其中一个实例关闭
- **THEN** 其他实例 SHALL 继续正常工作，继续接收事件

## ADDED Requirements

### Requirement: EventBus 生命周期管理
系统 SHALL 提供机制确保服务能正确注册和注销事件监听器，避免内存泄漏和意外行为。

#### Scenario: EventBus 支持单个监听器移除
- **GIVEN** 一个事件有多个监听器
- **WHEN** 调用 off(event, listener) 移除特定监听器
- **THEN** 只有指定的监听器被移除，其他监听器继续工作

#### Scenario: 服务清理时只移除自己的监听器
- **GIVEN** 一个服务注册了多个事件监听器
- **WHEN** 服务调用 cleanup 方法
- **THEN** 只有该服务注册的监听器被移除

### Requirement: RateLimiter 生命周期管理
Provider API SHALL 使用可管理的 RateLimiter 实例，支持生命周期控制。

#### Scenario: RateLimiter 注入到路由
- **GIVEN** Provider API 路由被创建
- **WHEN** RateLimiter 实例通过参数注入
- **THEN** 路由 SHALL 使用该实例进行限流检查

#### Scenario: RateLimiter 资源清理
- **GIVEN** Server 正在关闭
- **WHEN** RateLimiter.dispose() 被调用
- **THEN** 所有定时器和内存资源 SHALL 被清理
