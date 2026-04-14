## ADDED Requirements

### Requirement: AppContent 启动时通过 React Query 恢复 currentProjectId

AppContent SHALL 使用 `useProjects()` 和新增的 `useCurrentProject()` 两个 React Query hook 来加载项目状态，并通过 `useEffect` 将结果同步到 `ProjectContext`。ProjectProvider 本身 SHALL 不直接发起任何网络请求，仅作为纯上下文容器。

#### Scenario: 后端有 currentProject 时恢复
- **WHEN** AppContent 挂载
- **AND** `useProjects()` 返回非空列表
- **AND** `useCurrentProject()` 返回项目数据
- **THEN** `ProjectContext` 的 `projectId` SHALL 被设置为 currentProject 的 id
- **AND** `projects` 列表 SHALL 被同步

#### Scenario: 后端无 currentProject 但存在项目列表
- **WHEN** AppContent 挂载
- **AND** `useProjects()` 返回非空列表
- **AND** `useCurrentProject()` 返回错误或 404
- **THEN** `projectId` SHALL 被临时设置为列表中第一个项目的 id
- **AND** 不 SHALL 在后台自动调用 `POST /api/projects/:name/use`

#### Scenario: 后端无任何项目
- **WHEN** AppContent 挂载
- **AND** `useProjects()` 返回空列表
- **THEN** `projectId` SHALL 保持 null
- **AND** `projects` SHALL 为空数组

#### Scenario: API 请求失败
- **WHEN** AppContent 挂载
- **AND** `useProjects()` 抛出错误
- **THEN** `projectId` SHALL 保持 null
- **AND** `projects` SHALL 为空数组
- **AND** 不 SHALL 抛出未捕获异常导致白屏

### Requirement: ProjectGuard 全局守卫

应用 SHALL 包含一个 ProjectGuard 组件，在路由渲染前确保项目状态已就绪。无项目时显示引导页面。

#### Scenario: 项目加载中
- **WHEN** ProjectGuard 挂载
- **AND** projects 数据尚未加载完成
- **THEN** SHALL 显示全屏 loading 状态（非 "Loading..." 文本，使用 spinner 或骨架屏）

#### Scenario: 加载完成但无项目
- **WHEN** ProjectGuard 挂载
- **AND** projects 加载完成且列表为空
- **THEN** SHALL 显示引导页面，包含 "No projects yet" 提示和 "Create Project" 按钮
- **AND** 点击 "Create Project" SHALL 弹出 CreateProjectDialog

#### Scenario: 加载完成有项目但 projectId 为 null
- **WHEN** ProjectGuard 挂载
- **AND** projects 加载完成且列表非空
- **AND** projectId 为 null
- **THEN** SHALL 自动选择第一个项目并设置 projectId

#### Scenario: 加载完成有项目且 projectId 存在
- **WHEN** ProjectGuard 挂载
- **AND** projects 加载完成且列表非空
- **AND** projectId 不为 null
- **THEN** SHALL 直接渲染子路由（<Outlet />）

#### Scenario: 创建项目后自动选择
- **WHEN** ProjectGuard 显示引导页面
- **AND** 用户通过 CreateProjectDialog 成功创建项目
- **THEN** ProjectGuard SHALL 自动设置 projectId 为新项目 id
- **AND** 自动切换到正常路由视图

### Requirement: Settings 页面不受 ProjectGuard 阻塞

Settings 页面 SHALL 在无项目时仍可访问，不受 ProjectGuard 的"无项目引导"逻辑影响。

#### Scenario: 无项目时访问 Settings
- **WHEN** 用户访问 `/settings` 路由
- **AND** projects 为空
- **THEN** Settings 页面 SHALL 正常渲染
- **AND** 不显示项目引导页面
