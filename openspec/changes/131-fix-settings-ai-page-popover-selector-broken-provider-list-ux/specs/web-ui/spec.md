## MODIFIED Requirements

### Requirement: Model Select Popover 正确渲染

Settings AI 页面的 ModelSelect 组件 SHALL 使用与 `@headlessui/react` v2 兼容的 API。`Popover.Panel` SHALL 在用户点击按钮时正确渲染到 DOM 中，支持模型搜索、键盘导航和选择。

#### Scenario: 点击按钮打开 Mohist Model 选择器

- **WHEN** 用户点击 Mohist Model 选择器按钮
- **THEN** Popover Panel 渲染到 DOM 并显示模型列表
- **AND** 搜索输入框自动获得焦点

#### Scenario: 点击按钮打开 Coder Model 选择器

- **WHEN** 用户点击 Coder Model 选择器按钮
- **THEN** Popover Panel 渲染到 DOM 并显示模型列表
- **AND** 搜索输入框自动获得焦点

#### Scenario: Stage Model Overrides 选择器正常工作

- **WHEN** 用户展开 Stage Model Overrides
- **AND** 点击任一 stage 的模型选择器按钮
- **THEN** Popover Panel 渲染到 DOM 并显示模型列表

#### Scenario: 选择模型后关闭 Popover

- **WHEN** Popover Panel 已打开
- **AND** 用户点击一个模型选项
- **THEN** 选择器更新为所选模型
- **AND** Popover Panel 关闭

#### Scenario: 键盘导航选择模型

- **WHEN** Popover Panel 已打开
- **AND** 用户按 ArrowDown / ArrowUp 键
- **THEN** 高亮在模型列表中移动
- **AND** 用户按 Enter 键选中当前高亮的模型
