## MODIFIED Requirements

### Requirement: WebUI 支持通过对话框创建项目

WebUI SHALL 提供 `CreateProjectDialog` 组件，允许用户输入项目名称并通过 `DialogSelectDirectory` 选择工作目录路径来创建新项目。创建成功后 SHALL 自动切换到新项目并刷新项目列表。

#### Scenario: 成功创建项目
- **WHEN** 用户在 Header 下拉菜单点击 "New Project"
- **AND** 在对话框中输入名称 "my-project"
- **AND** 通过目录浏览器选择路径 "/home/user/repos/my-project"
- **AND** 点击 "Create"
- **THEN** 发送 `POST /api/projects` 请求（body: `{name, path}`）
- **AND** 项目列表自动刷新
- **AND** 当前项目自动切换到新创建的项目

#### Scenario: 创建项目名称已存在
- **WHEN** 用户输入已存在的项目名称
- **AND** 点击 "Create"
- **THEN** 后端返回 409 错误
- **AND** 对话框显示错误提示 "Project name already exists"
- **AND** 对话框保持打开状态

#### Scenario: 路径字段为空（验证失败）
- **WHEN** 用户只输入名称，未选择路径
- **AND** 点击 "Create"
- **THEN** 前端验证阻止提交
- **AND** 显示 "Path is required" 错误提示
- **AND** 不发送 API 请求

### Requirement: 无项目时显示空状态引导

WebUI SHALL 在没有项目时显示空状态引导页面，替代看板视图和 "Loading..." 文本。

#### Scenario: 首次访问无项目
- **WHEN** 用户打开 WebUI
- **AND** 项目列表为空
- **THEN** 显示空状态页面，包含提示文字 "No projects yet"
- **AND** 显示 "Create Project" 按钮

#### Scenario: 从空状态创建项目
- **WHEN** 用户在空状态页面点击 "Create Project"
- **THEN** 弹出 `CreateProjectDialog`
- **AND** 创建成功后自动切换到看板视图

#### Scenario: 无项目时访问 Explore 页面
- **WHEN** 用户访问 `/explore` 路由
- **AND** 项目列表为空
- **THEN** SHALL 显示空状态引导页面（与首页一致的引导）
- **AND** 不 SHALL 显示 "Loading..." 文本

## ADDED Requirements

### Requirement: Settings 页面路由使用 path param

Settings 页面路由 SHALL 从 `?tab=` query param 改为 `/settings/:section` path param。`/settings` 无 section 时重定向到 `/settings/ai`。支持的 section 值为：`ai`、`agent`、`system`。

#### Scenario: Settings 路由注册
- **WHEN** App.tsx 路由配置被检查
- **THEN** 包含 `<Route path="/settings" element={<Navigate to="/settings/ai" replace />} />` 或等效重定向
- **AND** 包含 `<Route path="/settings/:section" element={<SettingsPage />} />`

#### Scenario: 无效 section 重定向
- **WHEN** 用户访问 `/settings/invalid-section`
- **THEN** 重定向到 `/settings/ai`

#### Scenario: Header Settings 链接指向 AI section
- **WHEN** 用户点击 Header 中的 Settings 链接
- **THEN** 导航到 `/settings/ai`
