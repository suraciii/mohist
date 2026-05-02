## ADDED Requirements

### Requirement: Model Select Popover 正常打开和关闭

`ModelSelect` 组件 SHALL 使用 Headless UI v2 的 `Popover.Panel` 直接渲染，不使用 `Transition` 包裹。Popover SHALL 在用户点击按钮时打开，点击外部或选择模型时关闭。

#### Scenario: 点击按钮打开 popover

- **WHEN** 用户点击 ModelSelect 的触发按钮
- **THEN** Popover.Panel 渲染到 DOM 并可见
- **AND** 显示可用模型列表

#### Scenario: 点击外部关闭 popover

- **WHEN** Popover 处于打开状态
- **AND** 用户点击 Popover 外部区域
- **THEN** Popover 关闭，Panel 从 DOM 移除

#### Scenario: 选择模型后关闭 popover

- **WHEN** 用户在 Popover 中点击一个模型选项
- **THEN** 调用 `onChange` 回调并传入所选模型 ID
- **AND** Popover 关闭

### Requirement: Model Select Popover 支持搜索过滤

Popover 打开后 SHALL 显示搜索输入框，用户输入时实时过滤模型列表。

#### Scenario: 搜索过滤模型

- **WHEN** Popover 打开
- **AND** 用户在搜索框输入文本
- **THEN** 模型列表按 name 或 id 模糊匹配过滤
- **AND** 无匹配时显示 "No models found" 提示

#### Scenario: 搜索框自动聚焦

- **WHEN** Popover 打开
- **THEN** 搜索输入框自动获得焦点

### Requirement: Model Select Popover 支持键盘导航

Popover SHALL 支持上下箭头键高亮选项、Enter 键选择、搜索时重置高亮位置。

#### Scenario: 箭头键导航

- **WHEN** Popover 打开
- **AND** 用户按 ArrowDown
- **THEN** 高亮下移一项
- **AND** 用户按 ArrowUp 时高亮上移一项，不低于 0

#### Scenario: Enter 键选择

- **WHEN** Popover 打开
- **AND** 某个模型处于高亮状态
- **AND** 用户按 Enter
- **THEN** 选中高亮的模型，调用 `onChange` 并关闭 Popover

### Requirement: Model Select Popover 按分组显示模型

模型 SHALL 按 provider（模型 ID 的 `/` 前缀）分组显示，每组有 provider 名称标题。

#### Scenario: 模型按 provider 分组

- **WHEN** Popover 打开且有多个 provider 的模型
- **THEN** 模型按 provider 分组显示
- **AND** 每组有 provider 名称标题
