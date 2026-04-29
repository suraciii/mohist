## ADDED Requirements

### Requirement: 前端 API client 提供 config 方法

`api.ts` SHALL 添加 `getConfig` 和 `updateConfig` 方法，分别对应后端 `GET /api/config` 和 `PUT /api/config/:key`。

#### Scenario: getConfig 调用成功

- **WHEN** 调用 `api.getConfig()`
- **THEN** 发送 `GET /api/config` 请求
- **AND** 返回 `{ agentTimeout, maxConcurrentAgents, pollInterval }` 对象

#### Scenario: updateConfig 调用成功

- **WHEN** 调用 `api.updateConfig('agent.timeout', 2700000)`
- **THEN** 发送 `PUT /api/config/agent.timeout` 请求，body 为 `{ value: 2700000 }`
- **AND** 返回更新后的完整 `GeneralConfig` 对象

#### Scenario: updateConfig 验证失败

- **WHEN** 调用 `api.updateConfig('agent.timeout', 100)`
- **AND** 后端返回 400 验证错误
- **THEN** 抛出包含错误信息的异常

### Requirement: 前端 types 定义 Config 接口

`types.ts` SHALL 添加 `GeneralConfig` 接口，包含 `agentTimeout`（number, ms）、`maxConcurrentAgents`（number）、`pollInterval`（number, ms）字段。

#### Scenario: GeneralConfig 类型定义

- **WHEN** 检查 `types.ts` 导出
- **THEN** 包含 `GeneralConfig` 接口
- **AND** 接口包含 `agentTimeout: number`、`maxConcurrentAgents: number`、`pollInterval: number` 字段

### Requirement: 前端 hooks 提供 useConfig

`useQueries.ts` 或 `hooks/` 目录 SHALL 新增 `useConfig` hook，封装 config 的获取和更新逻辑。

#### Scenario: useConfig 初始加载

- **WHEN** 组件调用 `useConfig()`
- **THEN** 自动发起 `GET /api/config` 请求
- **AND** 返回 `{ config, isLoading, error, updateConfig }` 对象
- **AND** `config` 类型为 `GeneralConfig | null`

#### Scenario: useConfig updateConfig

- **WHEN** 调用 `useConfig()` 返回的 `updateConfig('agent.timeout', 2700000)`
- **THEN** 发送 `PUT /api/config/agent.timeout` 请求
- **AND** 成功后自动刷新 config 数据（触发 GET 重新获取）
- **AND** 返回 mutation 状态

#### Scenario: useConfig updateConfig 失败后回滚

- **WHEN** `updateConfig` 调用失败
- **THEN** 乐观更新被回滚
- **AND** `error` 字段包含错误信息
