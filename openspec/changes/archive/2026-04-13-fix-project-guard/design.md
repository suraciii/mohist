## Context

当前前端的项目状态管理存在结构性问题：

```
现状（有缺陷）:

  App
  └── ProjectProvider          ← projectId = useState(null)，纯内存
      └── AppContent
          ├── Header            ← 读 projectId，可切换项目
          └── Routes
              ├── /           → KanbanView        ← 唯一调用 setProjectId() 的地方
              ├── /explore    → ExploreRedirect   ← 假设 projectId 已存在，否则永久 Loading
              ├── /explore/:id → ExplorePage      ← 同上
              └── /issue/:number → IssueDetailPage ← 不依赖 projectId，但 SSE 需要
```

后端已有 `GET /api/projects/current`（读取 config 表的 `currentProjectId`）和 `POST /api/projects/:name/use`（设置 currentProjectId），但前端只在 Header 切换项目时调用 `useProject` mutation，从未用 `current` 端点来初始化。

核心问题链：
1. ProjectContext 初始值 `null`，刷新即丢失
2. 只有 KanbanView 的 useEffect 会设置 projectId
3. 直接访问 `/explore` 时 KanbanView 未挂载，projectId 永远是 null

## Goals / Non-Goals

**Goals:**
- 用户刷新任意页面后，前端能从后端恢复 currentProjectId
- 直接访问 `/explore` 或 `/explore/:id` 不再卡在 Loading
- 无项目时，所有页面统一引导用户创建项目
- 切换项目后，后端 currentProjectId 同步更新

**Non-Goals:**
- 不修改后端 API（已有足够的端点）
- 不引入新的全局状态管理库
- 不改变现有的路由结构
- 不处理多标签页同步问题

## Decisions

### Decision 1: AppContent 层统一初始化项目状态

**选择**: 在 `AppContent` 组件中使用 React Query（`useProjects()` 和新增的 `useCurrentProject()`）加载数据，并通过 `useEffect` 将结果同步到 `ProjectContext`。`ProjectProvider` 保持纯上下文角色，不直接发起 fetch。

**替代方案 A**: 在 ProjectProvider 中直接用 `useEffect + fetch`——被否决，因为这绕过了 TanStack Query 的缓存、重试和去重机制，会导致重复请求和竞态条件。
**替代方案 B**: 在每个需要 projectId 的组件中独立调用——被否决，会导致重复请求和竞态条件。

**理由**: 所有服务端状态已经由 React Query 统一管理。新增 `useCurrentProject` hook 复用了这套基础设施，初始化逻辑集中在一处（AppContent），又不破坏现有的数据流架构。

```
改造后:

  App
  └── ProjectProvider          ← 纯 Context，不 fetch
      └── AppContent
          │  启动时:
          │  1. useProjects()         → setProjects()
          │  2. useCurrentProject()   → setProjectId()
          │
          ├── Header
          └── ProjectGuard
              └── Routes
                  ├── /           → KanbanView   ← 不再负责初始化
                  ├── /explore    → ExploreRedirect
                  └── ...
```

### Decision 2: 添加 ProjectGuard 组件统一守卫

**选择**: 在 `AppContent` 的 `<Routes>` 外层包裹 `<ProjectGuard>`，当 projects 加载完成且为空时显示引导页面。

**替代方案 A**: 在每个路由组件中单独检查——被否决，重复逻辑。
**替代方案 B**: 用 React Router 的 loader 实现路由守卫——被否决，增加复杂度且与现有模式不一致。

**理由**: 守卫是单一位置的横切关注点。ProjectGuard 只处理"无项目"的全屏引导，不影响有项目时的路由渲染。

```
ProjectGuard 逻辑:

  loading? → 显示全屏 loading spinner
  loaded + 无项目 → 显示引导创建项目页面
  loaded + 有项目 + projectId 为 null → 自动选择第一个项目
  loaded + 有项目 + projectId 存在 → 渲染 <Outlet />
```

### Decision 3: Header 切换项目同步后端

**选择**: Header 中 `handleSelect` 已经同时调用了 `setProjectId(project.id)` 和 `switchProject.mutate(name)`（后者调用 `POST /projects/:name/use` 更新后端）。保持现状即可，无需改动。

### Decision 4: ExploreRedirect 简化

**选择**: ExploreRedirect 不再需要处理"无项目"分支（由 ProjectGuard 兜底），只需处理正常的 session 查找/创建流程。但仍需保留 `projectId` 的 null 检查作为防御性编程。

### Decision 5: Settings 页面豁免

**选择**: ProjectGuard 内部通过 `useLocation` 判断当前路由。如果是 `/settings`，则直接渲染 `<Outlet />`，不做项目状态拦截。Settings 页面不依赖 `projectId` 即可工作。

**替代方案**: 将 Settings route 放到 ProjectGuard 之外——被否决，因为它会丢失公共布局（如 Header 等外层 DOM 结构），且路由打散不利于维护。

**理由**: 路由豁免是轻量级的条件分支，守卫的职责边界仍然是"需要项目才能工作"，豁免 `/settings` 是合理的特例。

## Risks / Trade-offs

- **[启动多一次 API 调用]** → `useCurrentProject()` 会额外请求 `GET /projects/current`。可接受，因为 TanStack Query 会缓存，且只在应用启动时触发一次。
- **[后端 current 为 null 时的行为]** → `GET /projects/current` 返回 404 时，前端可以临时使用第一个项目作为 UI 上下文，但**不调用** `POST /projects/:name/use` 写后端。用户显式切换项目时，再同步后端。
- **[ProjectGuard 引入路由级条件渲染]** → 如果守卫逻辑变复杂，可能影响路由的可预测性。当前只处理"加载中/无项目/有项目"三种状态，且对 `/settings` 做单一豁免，复杂度可控。
