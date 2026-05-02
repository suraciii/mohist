## MODIFIED Requirements

### Requirement: ModelSelect Popover 兼容 Headless UI v2

ModelSelect 组件 SHALL 在 `@headlessui/react` v2 下正确渲染可交互的 Popover 下拉面板。Popover.Panel SHALL 在用户点击触发按钮后出现在 DOM 中并可见。

#### Scenario: 点击 ModelSelect 按钮后下拉面板渲染

- **WHEN** 用户点击 ModelSelect 的触发按钮
- **THEN** Popover.Panel SHALL 渲染到 DOM 中
- **AND** 面板 SHALL 可见且不被 `opacity-0` 或 `hidden` 样式隐藏
- **AND** 面板 SHALL 包含搜索输入框和模型列表

#### Scenario: 选择模型后面板关闭

- **WHEN** 下拉面板已打开
- **AND** 用户点击列表中的某个模型
- **THEN** 面板 SHALL 关闭
- **AND** 选中的模型 SHALL 显示在触发按钮中

#### Scenario: Stage Model Overrides 中的 ModelSelect 正常工作

- **WHEN** 用户展开 Stage Model Overrides 区域
- **AND** 点击某个 stage 的 ModelSelect 触发按钮
- **THEN** 该 stage 的 Popover.Panel SHALL 正常渲染并可选择模型
