## ADDED Requirements

### Requirement: Provider list visual grouping

AI Settings 页面的 provider 列表 SHALL 将 provider 分为两个视觉组：已配置（configured）和未配置（unconfigured）。已配置 providers SHALL 显示在列表顶部，未配置 providers SHALL 显示在下方。

#### Scenario: 混合状态的 provider 列表渲染
- **WHEN** AI Settings 页面加载
- **AND** 存在已配置和未配置的 providers
- **THEN** 已配置 providers 显示在列表顶部，带 "Configured" 分组标题
- **AND** 未配置 providers 显示在下方，带 "Available Providers" 分组标题

#### Scenario: 无已配置 provider
- **WHEN** AI Settings 页面加载
- **AND** 所有 providers 均未配置
- **THEN** 只显示 "Available Providers" 分组
- **AND** 不显示空的 "Configured" 分组

### Requirement: 未配置 provider 可折叠

未配置 providers 分组 SHALL 默认折叠，用户点击后展开。这减少了 80+ 未配置 provider 造成的视觉噪音。

#### Scenario: 默认折叠
- **WHEN** AI Settings 页面首次加载
- **THEN** "Available Providers" 分组处于折叠状态
- **AND** 只显示分组标题和 provider 数量

#### Scenario: 点击展开
- **WHEN** 用户点击 "Available Providers" 分组标题
- **THEN** 展开显示所有未配置 providers
- **AND** 每个未配置 provider 显示 Connect 按钮

#### Scenario: 点击折叠
- **WHEN** 用户再次点击已展开的 "Available Providers" 分组标题
- **THEN** 分组折叠回只显示标题和数量

### Requirement: Provider 搜索过滤

AI Settings 页面 SHALL 提供 provider 搜索框，允许用户按名称或 ID 过滤 provider 列表。

#### Scenario: 搜索已配置 provider
- **WHEN** 用户在搜索框输入文本
- **AND** 文本匹配某个已配置 provider 的名称或 ID
- **THEN** 已配置分组只显示匹配的 providers

#### Scenario: 搜索未配置 provider
- **WHEN** 用户在搜索框输入文本
- **AND** "Available Providers" 分组处于展开状态
- **THEN** 未配置分组只显示匹配的 providers

#### Scenario: 清空搜索
- **WHEN** 用户清空搜索框
- **THEN** 所有 providers 恢复显示
