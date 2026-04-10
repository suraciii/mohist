## ADDED Requirements

### Requirement: 配置变更事件通知
系统 SHALL 在 Provider 配置变更时触发事件，通知相关服务重新加载配置。

#### Scenario: Provider 配置保存后触发事件
- **WHEN** 用户通过 Web UI 或 CLI 保存 Provider 配置
- **THEN** ConfigService 触发 `config:providers:changed` 事件，携带新的配置数据

### Requirement: AgentRunner 热重载
AgentRunnerService SHALL 监听 Provider 配置变更事件，动态重新初始化 LLM Provider。

#### Scenario: 配置变更后自动重载
- **GIVEN** AgentRunnerService 正在运行
- **WHEN** 收到 `config:providers:changed` 事件
- **THEN** AgentRunnerService 使用新配置重新初始化 LLM Client

### Requirement: 热重载不影响正在运行的 Agent
系统 SHALL 确保配置热重载不会影响正在运行的 Agent 实例。

#### Scenario: Agent 运行期间配置变更
- **GIVEN** 有 Agent 正在处理 Issue
- **WHEN** Provider 配置发生变更
- **THEN** 当前 Agent 继续使用旧配置完成处理，下次启动时使用新配置

### Requirement: 配置缓存失效
ConfigService SHALL 在配置变更时清除内部缓存，确保后续读取获得最新配置。

#### Scenario: 配置更新后读取
- **GIVEN** ConfigService 已缓存配置
- **WHEN** Provider 配置被更新
- **THEN** 缓存被清除，下次 `getConfig()` 返回最新配置
