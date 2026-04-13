## Why

mohist 的业务模型要求用户选定一个项目才能工作，但前端 ProjectContext 是纯内存状态，刷新即丢失。后端已有 `currentProjectId` 持久化机制（`GET /projects/current`），前端却从未使用。导致直接访问 `/explore` 或刷新子路由时，projectId 为 null，Explore 页面永久卡在 "Loading..."。

## What Changes

- **ProjectProvider 启动时从后端恢复 currentProjectId**：调用 `GET /api/projects/current` 初始化，不再依赖 KanbanView 的 useEffect 副作用
- **添加全局 ProjectGuard 组件**：在路由层之上统一检查项目状态，无项目时引导用户创建/选择项目，所有子路由不再需要单独处理"无项目"逻辑
- **修复 ExploreRedirect 的 projectId 依赖**：移除对"先访问 / 初始化 projectId"的隐式依赖
- **KanbanView 简化**：移除 projectId 初始化逻辑，改为在 ProjectProvider 或 ProjectGuard 中处理

## Capabilities

### New Capabilities

- `project-guard`: 全局项目守卫层——在路由渲染前确保 currentProjectId 已从后端恢复，无项目时显示引导页面。涵盖 ProjectProvider 初始化逻辑和 ProjectGuard 守卫组件。

### Modified Capabilities

- `web-ui`: 移除 KanbanView 中的 projectId 初始化副作用；ExploreRedirect 不再需要独立处理无项目状态；Header 项目选择联动后端 setCurrent。

## Impact

- `packages/cli/web/src/context/ProjectContext.tsx`：无改动，保持纯上下文角色
- `packages/cli/web/src/App.tsx`：添加 ProjectGuard 和 AppContent 初始化逻辑（通过 React Query），移除 KanbanView 的初始化副作用
- `packages/cli/web/src/components/ExploreRedirect.tsx`：移除 loading 死角
- `packages/cli/web/src/components/Header.tsx`：切换项目时同步后端（已有逻辑，保持现状）
- `packages/cli/web/src/hooks/useQueries.ts`：新增 `useCurrentProject` query hook
- 无后端变更（`GET /projects/current` 已存在）
