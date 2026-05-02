## ADDED Requirements

### Requirement: AI Settings 页面布局优先级

AI Settings 页面 SHALL 按使用频率排列区域：Model Selection SHALL 出现在 Provider 列表之上。页面布局顺序 SHALL 为：Model Selection → Connected Providers → Available Providers → Custom Providers → Stage Model Overrides。

#### Scenario: 页面区域顺序

- **WHEN** 用户打开 Settings > AI 页面
- **THEN** Model Selection 区域（Mohist Model、Coder Model）SHALL 是 Providers 区域之前的第一个可交互区域
- **AND** Connected Providers 区域出现在 Model Selection 之后
- **AND** Available Providers 区域出现在 Connected Providers 之后

### Requirement: Provider 列表视觉分组

Provider 列表 SHALL 将已配置和未配置的 provider 分成明确的视觉分组。未配置的 provider SHALL 默认折叠在一个可展开的区域中。

#### Scenario: 已配置 provider 独立分组显示

- **WHEN** 存在已配置（connected）的 provider
- **THEN** 这些 provider SHALL 显示在 "Connected" 分组中
- **AND** 每个已配置 provider 显示 Remove 按钮
- **AND** 该分组默认展开

#### Scenario: 未配置 provider 默认折叠

- **WHEN** 存在未配置的 provider
- **THEN** 这些 provider SHALL 显示在一个标题为 "Available Providers" 的可折叠区域中
- **AND** 该区域默认折叠
- **AND** 标题旁 SHALL 显示未配置 provider 的数量（如 "Available Providers (78)"）
- **AND** 点击标题 SHALL 展开/折叠该区域

#### Scenario: 无未配置 provider 时不显示折叠区域

- **WHEN** 所有内置 provider 都已配置
- **THEN** 不显示 "Available Providers" 折叠区域

#### Scenario: 无已配置 provider 时显示空状态提示

- **WHEN** 没有任何已配置的 provider
- **THEN** Connected 分组 SHALL 显示提示信息引导用户连接一个 provider
