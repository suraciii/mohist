## Context

Mohist WebUI 使用 React + TanStack React Query + Tailwind CSS 构建，通过 Hono 后端的 REST API 交互。当前后端已有完整的项目管理 API（创建、列表、删除、切换），但前端只使用了 `GET /api/projects` 用于 Header 的项目选择下拉。项目上下文通过 `ProjectContext` 管理，使用 `useState` 保存当前选中的 projectId。

## Goals / Non-Goals

**Goals:**
- 用户可以在 WebUI 创建和删除项目，无需依赖 CLI
- 无项目时显示引导状态而非卡在 "Loading..."
- 复用现有 `Dialog` 组件和 Tailwind 设计风格，保持 UI 一致性

**Non-Goals:**
- 项目编辑（改名/改路径）— 后端 API 路由未提供 PATCH 端点
- **~~Path 字段可选~~** — 后端 API 要求 path 必填（已修正）
- 内置文件浏览器 — Path 字段使用手动输入
- 多项目并行视图 — 保持当前单项目选择模式
- 项目统计/详情页

## Decisions

### 1. 创建入口放在 Header 项目下拉菜单

**选择**: Header 下拉菜单增加 "New Project" 操作，点击弹出 `CreateProjectDialog`

**替代方案**: 独立的 `/projects` 管理页面

**理由**: 项目数量通常较少（1-5个），独立页面过于厚重。下拉菜单操作轻量且上下文自然（用户已经在项目选择区域）。参考 OpenCode 的 "Open Project" 按钮也放在首页入口位置。

### 2. 删除操作加确认对话框

**选择**: 删除前弹出确认对话框，显示项目名和关联 issue 数量

**理由**: 删除是破坏性操作且级联删除 issues。后端 `DELETE /api/projects/:name` 已做级联处理，前端需要防止误操作。

### 3. 空状态在 KanbanBoard 层级处理

**选择**: 当 `projects` 为空数组时，在 KanbanBoard 位置渲染空状态引导，替代看板

**理由**: 空状态和看板是互斥的视图，在 App.tsx 中根据 `projects.length` 条件渲染即可。

### 4. API client 扩展现有 api 对象

**选择**: 在 `api.ts` 的 `api` 对象中添加 `createProject`、`deleteProject`、`useProject` 方法，使用 `useMutation` hook

**理由**: 与现有 `createIssue` 等模式一致，mutation 成功后 invalidate `['projects']` query cache。

## Risks / Trade-offs

- **[无后端改动]** → 零后端风险，但依赖现有 API 行为正确（已验证）
- **[Path 手动输入]** → 用户可能输入无效路径。缓解：Path 字段非必填，后端可接受空路径，用户后续通过 CLI 修改
- **[删除级联]** → 删除项目会级联删除所有 issues。缓解：确认对话框明确提示
