## ADDED Requirements

### Requirement: 移动端底部 Tab 导航

WebUI SHALL 提供 `MobileBottomNav` 组件，在视口宽度 <768px 时显示底部 Tab 导航栏，提供 Board、Explore、Settings 三个导航入口。

#### Scenario: 移动端显示底部导航栏
- **WHEN** 视口宽度 < 768px
- **THEN** 页面底部显示固定定位的 Tab 导航栏
- **AND** 导航栏包含 3 个 Tab: Board（路由 `/`）、Explore（路由 `/explore`）、Settings（路由 `/settings`）
- **AND** 当前路由对应的 Tab 高亮显示

#### Scenario: 桌面端隐藏底部导航栏
- **WHEN** 视口宽度 >= 768px
- **THEN** 底部 Tab 导航栏不渲染（`md:hidden`）

#### Scenario: Tab 切换导航
- **WHEN** 用户在移动端点击底部 Tab "Explore"
- **THEN** 导航到 `/explore` 路由
- **AND** "Explore" Tab 高亮

#### Scenario: 底部导航栏不遮挡内容
- **WHEN** 底部 Tab 导航栏显示
- **THEN** App 主容器底部有 `pb-14`（56px）padding
- **AND** 内容可正常滚动，不被导航栏遮挡

#### Scenario: 底部导航栏安全区域适配
- **WHEN** 设备有底部安全区域（如 iPhone 刘海屏）
- **THEN** 导航栏底部包含 `env(safe-area-inset-bottom)` padding
- **AND** 导航栏高度为 56px + safe-area-inset-bottom

### Requirement: Header 移动端简化

Header 组件在视口宽度 <768px 时 SHALL 只显示 logo 和项目选择器，隐藏所有导航按钮。

#### Scenario: 移动端 Header 只显示 logo 和项目选择器
- **WHEN** 视口宽度 < 768px
- **THEN** Header 只显示 logo 和项目选择器下拉菜单
- **AND** Explore、Logs、Settings、New Issue 按钮不渲染

#### Scenario: 桌面端 Header 保持完整
- **WHEN** 视口宽度 >= 768px
- **THEN** Header 显示所有导航按钮和操作按钮

### Requirement: FAB 浮动按钮

WebUI SHALL 提供 `FAB`（Floating Action Button）组件，在移动端看板页面显示浮动按钮，用于创建新 Issue。

#### Scenario: 移动端看板页面显示 FAB
- **WHEN** 视口宽度 < 768px
- **AND** 用户在看板页面（`/` 路由）
- **THEN** 页面右下角显示浮动 "+" 按钮
- **AND** 按钮位置在底部 Tab 导航栏上方

#### Scenario: 点击 FAB 创建 Issue
- **WHEN** 用户点击 FAB 按钮
- **THEN** 打开 CreateIssueDialog 对话框

#### Scenario: 桌面端不显示 FAB
- **WHEN** 视口宽度 >= 768px
- **THEN** FAB 不渲染

### Requirement: Kanban Board 移动端单列模式

KanbanBoard 在视口宽度 <768px 时 SHALL 改为横向 scrollable Stage tabs + 选中 Stage 的单列卡片视图。

#### Scenario: 移动端显示 Stage tabs
- **WHEN** 视口宽度 < 768px
- **THEN** KanbanBoard 顶部显示横向可滚动的 Stage tabs（Draft / Plan / Build / Check / Done）
- **AND** 选中的 Stage tab 高亮显示
- **AND** tabs 使用 scroll-snap 对齐

#### Scenario: 选中 Stage 显示单列卡片
- **WHEN** 视口宽度 < 768px
- **AND** 用户选中 "Build" Stage tab
- **THEN** 只显示 Build Stage 的 issue 卡片（单列布局）
- **AND** 其他 Stage 的卡片不显示

#### Scenario: 桌面端保持多列布局
- **WHEN** 视口宽度 >= 768px
- **THEN** KanbanBoard 保持原有的多列并排布局

#### Scenario: 默认选中第一个 Stage
- **WHEN** 用户在移动端首次加载看板页面
- **THEN** 默认选中第一个有卡片的 Stage tab

### Requirement: 页面内容移动端间距适配

所有页面在视口宽度 <768px 时 SHALL 使用 `px-4` 水平内边距，桌面端保持 `px-6`。

#### Scenario: 移动端间距
- **WHEN** 视口宽度 < 768px
- **THEN** IssueDetailPage、SettingsPage、LogsPage 水平内边距为 16px（px-4）

#### Scenario: 桌面端间距
- **WHEN** 视口宽度 >= 768px
- **THEN** 水平内边距为 24px（px-6）

### Requirement: 触摸目标尺寸

所有交互按钮在移动端 SHALL 保证最小触摸目标尺寸为 44x44px。

#### Scenario: 按钮触摸目标
- **WHEN** 视口宽度 < 768px
- **AND** 页面包含可点击按钮
- **THEN** 所有按钮的最小高度为 44px（min-h-[44px]）
- **AND** 所有按钮的最小宽度为 44px

### Requirement: Dialog 移动端全屏模式

Dialog 组件在视口宽度 <768px 时 SHALL 以全屏模式显示。

#### Scenario: 移动端 Dialog 全屏
- **WHEN** 视口宽度 < 768px
- **AND** Dialog 打开（如 CreateIssueDialog）
- **THEN** Dialog 占满整个视口
- **AND** 不显示遮罩层背后的内容

#### Scenario: 桌面端 Dialog 保持模态框
- **WHEN** 视口宽度 >= 768px
- **THEN** Dialog 保持原有的居中模态框样式

### Requirement: ExplorePage 移动端适配

ExplorePage 在移动端 SHALL 精简 ModelSelector 显示，并为输入框添加 safe-area padding。

#### Scenario: ModelSelector 移动端精简显示
- **WHEN** 视口宽度 < 768px
- **THEN** ModelSelector 显示精简格式（如只显示模型短名称）

#### Scenario: 输入框 safe-area padding
- **WHEN** 视口宽度 < 768px
- **AND** 设备有底部安全区域
- **THEN** 聊天输入框底部包含 safe-area-inset-bottom padding

### Requirement: viewport meta 配置

index.html SHALL 配置 viewport meta 支持 safe-area-inset 和全屏模式。

#### Scenario: viewport meta 配置
- **WHEN** 检查 index.html 的 viewport meta 标签
- **THEN** 包含 `viewport-fit=cover` 属性
- **AND** 包含 `theme-color` meta 标签

### Requirement: SettingsPage 移动端适配

SettingsPage 在移动端 SHALL 将 Provider 卡片改为垂直堆叠布局。

#### Scenario: Provider 卡片移动端堆叠
- **WHEN** 视口宽度 < 768px
- **THEN** Provider 配置卡片垂直堆叠显示（单列）
- **AND** 不使用水平并排布局
