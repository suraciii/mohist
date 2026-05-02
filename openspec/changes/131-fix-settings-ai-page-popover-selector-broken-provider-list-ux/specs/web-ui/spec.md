## MODIFIED Requirements

### Requirement: AI Settings Model Select Popover

ModelSelect 组件 SHALL 使用 Headless UI v2 兼容的 Popover.Panel 渲染方式。Popover.Panel SHALL 在用户点击按钮时正常渲染到 DOM 并显示模型列表。

#### Scenario: Mohist Model 选择器正常打开
- **WHEN** 用户在 AI Settings 页面点击 Mohist Model 选择器按钮
- **THEN** Popover.Panel 渲染到 DOM
- **AND** 显示已配置 provider 的模型列表（按 provider 分组）
- **AND** 用户可以选择一个模型

#### Scenario: Coder Model 选择器正常打开
- **WHEN** 用户在 AI Settings 页面点击 Coder Model 选择器按钮
- **THEN** Popover.Panel 渲染到 DOM
- **AND** 显示可用的 coder 模型列表
- **AND** 用户可以选择一个模型或清除当前选择

#### Scenario: Stage Model Override 选择器正常打开
- **WHEN** 用户展开 Stage Model Overrides 区域
- **AND** 点击某个 stage 的选择器按钮
- **THEN** Popover.Panel 渲染到 DOM
- **AND** 显示可用的 coder 模型列表
- **AND** 用户可以为该 stage 选择或清除模型

#### Scenario: Popover 搜索过滤
- **WHEN** Popover 打开后用户在搜索框输入文本
- **THEN** 模型列表实时按名称和 ID 过滤

### Requirement: AI Settings Provider 列表布局

AI Settings 页面的 Provider 列表 SHALL 有清晰的视觉层次，将已连接的 provider 与未配置的 provider 分开显示，并将 Model Selection 区域放在 Provider 列表之前。

#### Scenario: 已连接 provider 与未配置 provider 分开显示
- **WHEN** 用户打开 AI Settings 页面
- **THEN** 已连接的 provider 显示在独立分组中
- **AND** 未配置的 provider 显示在另一个可折叠或分离的分组中
- **AND** 两个分组有明确的视觉区分

#### Scenario: Model Selection 在页面上方
- **WHEN** 用户打开 AI Settings 页面
- **THEN** Model Selection 区域（Mohist Model、Coder Model）出现在 Provider 列表之前
- **AND** 用户无需滚动即可看到并操作模型选择器

#### Scenario: 未配置 provider 分组可折叠
- **WHEN** 未配置 provider 列表存在
- **THEN** 该分组默认折叠或收起
- **AND** 用户可以展开查看全部未配置 provider
