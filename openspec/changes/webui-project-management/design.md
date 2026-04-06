## Context

Mohist WebUI 使用 React + TanStack React Query + Tailwind CSS 构建，通过 Hono 后端的 REST API 交互。当前后端已有完整的项目管理 API（创建、列表、删除、切换），但前端只使用了 `GET /api/projects` 用于 Header 的项目选择下拉。项目上下文通过 `ProjectContext` 管理，使用 `useState` 保存当前选中的 projectId。

## Goals / Non-Goals

**Goals:**
- 用户可以在 WebUI 创建和删除项目，无需依赖 CLI
- 无项目时显示引导状态而非卡在 "Loading..."
- 搜索式目录浏览器选择项目路径（参考 OpenCode DialogSelectDirectory）
- 复用现有 `Dialog` 组件和 Tailwind 设计风格，保持 UI 一致性

**Non-Goals:**
- 项目编辑（改名/改路径）— 后端 API 路由未提供 PATCH 端点
- 多项目并行视图 — 保持当前单项目选择模式
- 项目统计/详情页
- 文件浏览（只浏览目录，不浏览文件内容）
- Desktop 原生文件夹选择器 — 纯 Web 实现

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

### 5. 路径选择使用搜索式目录浏览器

**选择**: `DialogSelectDirectory` 组件，支持路径搜索、Tab 补全、最近项目列表

**替代方案**: 手动文本输入 `<input type="text">`

**理由**: 手动输入路径体验差，用户需要精确记忆路径。搜索式目录浏览器让用户通过模糊搜索快速定位目录，大幅降低输入成本。参考 OpenCode 的实现（`DialogSelectDirectory`），功能经过大量用户验证。

### 6. 后端文件系统 API 设计

**选择**: 新增两个 API 端点，使用 Node.js `fs.readdir` + 系统 `find` 命令

```
GET /api/fs/list?path=/home/user/repos
  → fs.readdir(path, { withFileTypes: true })
  → 过滤: 只返回 directories，排除 .开头的隐藏目录
  → 返回: { name: string, absolute: string }[]

GET /api/fs/search?query=myapp&limit=50
  → find $HOME -type d -maxdepth 4 -iname "*query*"
  → 路径遍历防护: resolve 后必须在 HOME 下
  → 返回: { name: string, absolute: string }[]
```

**替代方案**: 引入 ripgrep 做搜索

**理由**: `find` 命令在所有 Unix 系统可用，零新后端依赖。maxdepth=4 限制搜索深度，覆盖绝大多数项目结构同时避免全盘扫描。

### 7. 模糊匹配在前端完成

**选择**: 前端引入 `fuzzysort`（~2KB），后端只负责列出/搜索目录

**理由**: 职责清晰 — 后端返回候选列表，前端做模糊排序和过滤。`fuzzysort` 非常轻量，OpenCode 也使用同样的库。

### 8. 浏览范围：HOME 起始，不限制

**选择**: 搜索起始点为用户 HOME 目录，路径输入支持绝对路径和 `~` 前缀

**理由**: mohist server 纯本地运行，用户本身有终端完整权限，不存在远程安全边界。限制浏览范围只会带来困扰（项目可能在 `/opt`、`/var` 等位置）。

### 9. DialogSelectDirectory 的交互设计

参考 OpenCode 实现，核心交互：

```
┌─────────────────────────────────────────┐
│  Select Project Directory          [X]  │
├─────────────────────────────────────────┤
│                                         │
│  🔍 ~/repos/my-app_                     │  ← 搜索输入框
│                                         │
│  ── Recent Projects ────────────────────│
│  📁 ~/repos/mohist/                     │  ← 已创建的项目
│  📁 ~/repos/other-app/                  │
│                                         │
│  ── Directories ─────────────────────── │
│  📁 ~/repos/my-app-backend/             │  ← 搜索结果
│  📁 ~/repos/my-app-frontend/            │
│  📁 ~/repos/my-app-api/                 │
│                                         │
│     [Cancel]            [Select]        │
└─────────────────────────────────────────┘
```

- 路径模式（含 `/` 或 `~`）：逐段调用 `/api/fs/list`，fuzzysort 匹配每段目录名
- 搜索模式（纯文本）：调用 `/api/fs/search`，fuzzysort 排序结果
- Tab 键：自动补全当前路径片段
- 选择后：填充 CreateProjectDialog 的 path 字段

## Risks / Trade-offs

- **[后端新增 API]** → 新增 `api/fs.ts`，需要路径遍历防护。缓解：resolve 后校验路径合法性
- **[find 命令跨平台]** → Windows 下 `find` 语义不同。缓解：Windows 下使用 `dir /s /ad /b`，或后续引入 ripgrep
- **[大目录性能]** → HOME 下目录可能很多。缓解：list API 只列当前层级；search API 限制 maxdepth=4 和 limit=50
- **[删除级联]** → 删除项目会级联删除所有 issues。缓解：确认对话框明确提示
