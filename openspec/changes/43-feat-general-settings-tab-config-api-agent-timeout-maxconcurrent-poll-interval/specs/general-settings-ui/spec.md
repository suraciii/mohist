## ADDED Requirements

### Requirement: General Settings tab 展示配置表单

SettingsPage 的 General tab SHALL 从 `GET /api/config` 加载当前配置，并以表单形式展示三个设置项：Agent Timeout（分钟）、Max Concurrent Agents（数量）、Poll Interval（秒）。每个字段 SHALL 显示当前值和描述文字。

#### Scenario: 首次加载显示当前配置

- **WHEN** 用户切换到 General tab
- **THEN** 调用 `GET /api/config` 获取配置
- **AND** Agent Timeout 字段显示 `agentTimeout / 60000`（转换为分钟）
- **AND** Max Concurrent Agents 字段显示 `maxConcurrentAgents`
- **AND** Poll Interval 字段显示 `pollInterval / 1000`（转换为秒）

#### Scenario: 加载中显示 loading 状态

- **WHEN** 配置数据正在加载
- **THEN** General tab 显示 loading 指示器
- **AND** 表单字段不可交互

#### Scenario: 加载失败显示错误

- **WHEN** `GET /api/config` 请求失败
- **THEN** General tab 显示错误提示信息
- **AND** 提供 "Retry" 按钮

### Requirement: General Settings 支持保存单个配置项

每个配置字段 SHALL 在用户修改并提交后，调用 `PUT /api/config/:key` 单独保存。保存成功后 SHALL 显示成功提示，保存失败 SHALL 显示错误信息并恢复原值。

#### Scenario: 修改 Agent Timeout 并保存

- **WHEN** 用户将 Agent Timeout 修改为 45 分钟
- **AND** 点击 Save 或字段失焦触发保存
- **THEN** 调用 `PUT /api/config/agent.timeout` with `{ value: 2700000 }`（转换为毫秒）
- **AND** 成功后显示成功提示
- **AND** 字段值保持为 45

#### Scenario: 修改 Max Concurrent Agents 并保存

- **WHEN** 用户将 Max Concurrent Agents 修改为 4
- **AND** 点击 Save
- **THEN** 调用 `PUT /api/config/agent.maxConcurrent` with `{ value: 4 }`
- **AND** 成功后显示成功提示

#### Scenario: 修改 Poll Interval 并保存

- **WHEN** 用户将 Poll Interval 修改为 60 秒
- **AND** 点击 Save
- **THEN** 调用 `PUT /api/config/poll.interval` with `{ value: 60000 }`（转换为毫秒）
- **AND** 成功后显示成功提示

#### Scenario: 保存失败恢复原值

- **WHEN** 用户修改某字段后保存
- **AND** API 返回错误
- **THEN** 显示错误信息
- **AND** 字段恢复为修改前的值

### Requirement: General Settings 前端字段验证

表单 SHALL 在提交前进行前端验证，阻止无效值发送到后端。

#### Scenario: Agent Timeout 小于 1 分钟

- **WHEN** 用户输入 Agent Timeout 为 0
- **THEN** 显示错误提示 "Must be at least 1 minute"
- **AND** 不发送 API 请求

#### Scenario: Max Concurrent Agents 超出范围

- **WHEN** 用户输入 Max Concurrent Agents 为 20
- **THEN** 显示错误提示 "Must be between 1 and 16"
- **AND** 不发送 API 请求

#### Scenario: Poll Interval 小于 5 秒

- **WHEN** 用户输入 Poll Interval 为 2
- **THEN** 显示错误提示 "Must be at least 5 seconds"
- **AND** 不发送 API 请求

#### Scenario: 输入非数字

- **WHEN** 用户在数字字段输入非数字文本
- **THEN** 显示错误提示 "Must be a valid number"
- **AND** 不发送 API 请求

### Requirement: General Settings 支持 Reset to Defaults

General tab SHALL 提供 "Reset to Defaults" 按钮，点击后重置所有配置为默认值。

#### Scenario: 重置配置

- **WHEN** 用户点击 "Reset to Defaults" 按钮
- **THEN** 弹出确认对话框
- **AND** 用户确认后，分别调用 PUT 更新三个配置项为默认值（timeout: 30 min, maxConcurrent: 8, pollInterval: 30 sec）
- **AND** 表单刷新显示默认值

#### Scenario: 取消重置

- **WHEN** 用户点击 "Reset to Defaults" 按钮
- **AND** 在确认对话框中点击取消
- **THEN** 不发送任何 API 请求
- **AND** 表单值保持不变
