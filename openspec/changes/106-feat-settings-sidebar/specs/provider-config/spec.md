## ADDED Requirements

### Requirement: 前端 Provider 列表统一展示

前端 Settings AI section SHALL 将已连接和未连接的 provider 合并为单一列表展示。排序规则：已连接 provider 优先，未连接 provider 在后。消除独立的 "Connected Providers" 和 "Available Providers" 分组区域。

#### Scenario: 合并列表排序
- **WHEN** API 返回 3 个已连接 provider 和 10 个未连接 provider
- **THEN** 列表前 3 项为已连接 provider（按名称排序）
- **AND** 后 10 项为未连接 provider（按名称排序）
- **AND** 不存在独立的 "Connected Providers" 或 "Available Providers" 分组标题

#### Scenario: 已连接 provider 操作
- **WHEN** 列表中的 provider 为已连接状态
- **THEN** 行内显示 masked API key + 来源标签（config/env）+ [Remove] 按钮

#### Scenario: 未连接 provider 操作
- **WHEN** 列表中的 provider 为未连接状态
- **THEN** 行内显示简短描述 + [Connect] 按钮

#### Scenario: 全部已连接
- **WHEN** 所有内置 provider 均已配置
- **THEN** 列表只显示已连接 provider
- **AND** 不显示空状态占位

#### Scenario: 全部未连接
- **WHEN** 没有任何 provider 配置
- **THEN** 列表显示所有内置 provider 为未连接状态
- **AND** 每项均有 [Connect] 按钮
