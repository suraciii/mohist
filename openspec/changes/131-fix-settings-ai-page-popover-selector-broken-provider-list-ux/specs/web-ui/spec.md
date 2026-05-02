## ADDED Requirements

### Requirement: Model Select Popover 正确渲染

`ModelSelect` 组件 SHALL 在 Headless UI v2（`@headlessui/react` 2.x）下正确渲染 `Popover.Panel`。组件 SHALL NOT 使用缺少 `show` prop 的 `Transition` 包裹 `Popover.Panel`。

#### Scenario: 点击 Model Select 按钮打开 Popover

- **WHEN** 用户点击 Model Select 按钮（Mohist Model、Coder Model 或 Stage Override）
- **THEN** Popover.Panel 渲染到 DOM 并可见
- **AND** 显示搜索框和模型列表

#### Scenario: Popover 内搜索和选择模型

- **WHEN** Popover 打开后用户在搜索框输入文字
- **THEN** 模型列表按搜索词实时过滤
- **WHEN** 用户点击某个模型
- **THEN** 选择生效且 Popover 关闭

#### Scenario: Stage Model Override 选择器正常工作

- **WHEN** 用户展开 "Stage Model Overrides" 区域
- **AND** 点击某个 stage 的 Model Select 按钮
- **THEN** 该 stage 的 Popover 正常打开并可选择模型

### Requirement: AI Settings 页面布局优先显示 Model Selection

AI Settings 页面 SHALL 将 Model Selection 区域（Mohist Model 和 Coder Model）放置在 Provider 列表之前，确保用户最常用的配置项不被埋在页面底部。

#### Scenario: 页面渲染顺序

- **WHEN** 用户打开 Settings AI 页面
- **THEN** Model Selection 区域（Mohist Model、Coder Model）显示在 Provider 列表之前

### Requirement: Provider 列表按连接状态分组显示

Provider 列表 SHALL 按连接状态分组：已连接的 Provider 显示在顶部，未配置的 Provider 折叠或以次要视觉层次显示。

#### Scenario: 已连接 Provider 显示在顶部

- **WHEN** 至少有一个已连接的 Provider
- **THEN** 已连接的 Provider 列表显示在 Provider 区域的顶部
- **AND** 每个 Provider 显示名称、连接状态和 Remove 按钮

#### Scenario: 未配置 Provider 折叠显示

- **WHEN** 存在未配置的 Provider
- **THEN** 未配置 Provider 显示在已连接 Provider 下方
- **AND** 默认折叠，用户可展开查看完整列表
- **AND** 折叠状态显示未配置 Provider 的数量（如 "80 providers available"）

#### Scenario: 全部已连接时无折叠区域

- **WHEN** 所有 Provider 都已连接（无未配置 Provider）
- **THEN** 不显示折叠的未配置区域
