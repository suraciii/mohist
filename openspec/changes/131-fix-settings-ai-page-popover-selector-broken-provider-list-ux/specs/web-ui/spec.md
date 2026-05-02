## ADDED Requirements

### Requirement: ModelSelect Popover 可正常打开

AI Settings 页面的 `ModelSelect` 组件 SHALL 使用 Headless UI v2 兼容的方式渲染 `Popover.Panel`，确保所有模型选择器（Mohist Model、Coder Model、Stage Model Overrides）可以正常打开和选择模型。

#### Scenario: Mohist Model 选择器打开
- **WHEN** 用户点击 Mohist Model 选择器按钮
- **THEN** Popover Panel 渲染到 DOM 并显示可选模型列表
- **AND** 用户可以搜索、高亮和选择一个模型

#### Scenario: Coder Model 选择器打开
- **WHEN** 用户点击 Coder Model 选择器按钮
- **THEN** Popover Panel 渲染到 DOM 并显示可选模型列表
- **AND** 用户可以搜索、高亮和选择一个模型

#### Scenario: Stage Model Override 选择器打开
- **WHEN** 用户展开 Stage Model Overrides
- **AND** 点击某个 stage 的模型选择器按钮
- **THEN** Popover Panel 渲染到 DOM 并显示可选模型列表
- **AND** 用户可以搜索、高亮和选择一个模型

#### Scenario: 清除已选模型
- **WHEN** ModelSelect 配置了 `allowClear` 且已有选中值
- **THEN** 显示清除按钮（X 图标）
- **AND** 点击清除按钮后值被清空

### Requirement: AI Settings 页面布局 Model Selection 在上

AI Settings 页面 SHALL 将 Model Selection 区域（Mohist Model、Coder Model、Stage Model Overrides）放在页面顶部、Provider 列表之前。

#### Scenario: 页面布局顺序
- **WHEN** 用户打开 AI Settings 页面
- **THEN** Model Selection 区域（包括 Mohist Model、Coder Model 选择器）显示在页面顶部
- **AND** Provider 列表显示在 Model Selection 下方
