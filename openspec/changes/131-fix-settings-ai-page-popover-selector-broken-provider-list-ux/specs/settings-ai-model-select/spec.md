## ADDED Requirements

### Requirement: ModelSelect popover opens on click

ModelSelect 组件 SHALL 使用 `@headlessui/react` v2 兼容的方式渲染 `Popover.Panel`。Panel SHALL 不被 `Transition` 包裹（或传入 `show={open}`），确保点击按钮时 Panel 正常渲染到 DOM 并可见。

#### Scenario: 点击 Mohist Model 选择器打开 Popover

- **WHEN** 用户点击 AI Settings 页面的 "Mohist Model" 选择器按钮
- **THEN** Popover Panel 渲染到 DOM 并显示可用模型列表
- **AND** 用户可以从列表中选择一个模型

#### Scenario: 点击 Coder Model 选择器打开 Popover

- **WHEN** 用户点击 AI Settings 页面的 "Coder Model" 选择器按钮
- **THEN** Popover Panel 渲染到 DOM 并显示可用模型列表
- **AND** 用户可以从列表中选择一个模型

#### Scenario: Stage Model Overrides 中的选择器正常打开

- **WHEN** 用户展开 Stage Model Overrides 区域
- **AND** 点击任意 stage 的模型选择器按钮
- **THEN** Popover Panel 渲染到 DOM 并显示可用模型列表
- **AND** 用户可以选择该 stage 的模型覆盖

### Requirement: ModelSelect 支持搜索过滤

ModelSelect Popover 内 SHALL 包含搜索输入框，用户输入时实时过滤模型列表。过滤 SHALL 同时匹配模型的 name 和 id 字段（不区分大小写）。

#### Scenario: 搜索过滤模型

- **WHEN** 用户在 Popover 搜索框中输入 "claude"
- **THEN** 模型列表只显示 name 或 id 中包含 "claude" 的模型（不区分大小写）

#### Scenario: 搜索无结果

- **WHEN** 用户在 Popover 搜索框中输入不匹配任何模型的文本
- **THEN** 显示 "No models found" 空状态提示

### Requirement: ModelSelect 支持键盘导航

ModelSelect SHALL 支持键盘操作：ArrowUp/ArrowDown 移动高亮，Enter 选择当前高亮项。搜索内容变化时高亮索引 SHALL 重置为 0。

#### Scenario: 键盘上下移动高亮

- **WHEN** Popover 打开且搜索框获得焦点
- **AND** 用户按 ArrowDown
- **THEN** 高亮移动到下一个模型
- **AND** 用户按 ArrowUp 时高亮移动到上一个模型

#### Scenario: Enter 键选择模型

- **WHEN** Popover 打开且某个模型处于高亮状态
- **AND** 用户按 Enter
- **THEN** 选中该高亮模型
- **AND** Popover 关闭

### Requirement: ModelSelect 按 Provider 分组显示

ModelSelect SHALL 将模型按 provider（模型 id 的第一段 `/` 前的部分）分组显示，每组显示 provider 名称作为小标题。

#### Scenario: 模型按 provider 分组

- **WHEN** Popover 打开且有多于一个 provider 的模型
- **THEN** 模型列表按 provider 分组
- **AND** 每组有一个 provider 名称小标题
