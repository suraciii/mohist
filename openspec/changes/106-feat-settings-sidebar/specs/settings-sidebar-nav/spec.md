## ADDED Requirements

### Requirement: Settings 页面使用 sidebar 导航布局

Settings 页面 SHALL 使用 sidebar 导航 + 内容区两栏布局替代现有的 top tabs。Sidebar 固定在视口左侧，内容区独立滚动。三个导航项按用户意图分区：AI、Agent、System。

#### Scenario: 默认显示 AI section
- **WHEN** 用户导航到 `/settings`
- **THEN** 自动重定向到 `/settings/ai`
- **AND** Sidebar 中 "AI" 项高亮
- **AND** 内容区显示 AI section

#### Scenario: 切换 sidebar 导航项
- **WHEN** 用户点击 sidebar 中的 "Agent" 项
- **THEN** URL 更新为 `/settings/agent`
- **AND** 内容区显示 Agent section
- **AND** Sidebar 中 "Agent" 项高亮，其他项取消高亮

#### Scenario: 直接访问 deep link
- **WHEN** 用户直接访问 `/settings/system`
- **THEN** 显示 System section
- **AND** Sidebar 中 "System" 项高亮

#### Scenario: 访问无效 section
- **WHEN** 用户访问 `/settings/unknown`
- **THEN** 重定向到 `/settings/ai`

### Requirement: Sidebar 在窄屏变为 dropdown selector

在窄屏（<768px）时，sidebar SHALL 隐藏，替换为顶部的 dropdown selector，包含相同的三个导航项。选择项后更新 URL 和内容区。

#### Scenario: 窄屏显示 dropdown
- **WHEN** 视口宽度 < 768px
- **THEN** 不显示 sidebar
- **AND** 显示顶部 dropdown selector，包含 AI / Agent / System 选项
- **AND** dropdown 当前选中项与 URL 匹配

#### Scenario: 窄屏切换 section
- **WHEN** 用户在窄屏 dropdown 中选择 "Agent"
- **THEN** URL 更新为 `/settings/agent`
- **AND** 内容区显示 Agent section

#### Scenario: 宽屏显示 sidebar
- **WHEN** 视口宽度 >= 768px
- **THEN** 显示 sidebar 导航
- **AND** 不显示 dropdown selector

### Requirement: Sidebar 持久可见且内容区独立滚动

Sidebar SHALL 在内容区滚动时保持固定位置（sticky 或 fixed），不随内容滚动。内容区 SHALL 独立滚动。

#### Scenario: 内容区滚动时 sidebar 不动
- **WHEN** 内容区内容超出视口高度
- **AND** 用户滚动内容区
- **THEN** sidebar 保持可见，不随内容滚动

#### Scenario: 侧边栏导航项高亮当前 section
- **WHEN** 当前 URL 为 `/settings/agent`
- **THEN** sidebar 的 "Agent" 项显示活跃状态（高亮背景 + 文字颜色）
- **AND** 其他项显示非活跃状态
