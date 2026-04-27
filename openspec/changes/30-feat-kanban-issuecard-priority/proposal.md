## Why

Kanban IssueCard 当前只展示 3/13 个后端字段（number、title、labels），信息密度 23%。用户无法在卡片上看到 priority、问题信号（merge conflict、interrupted、closed）、类型颜色或时间上下文，导致看板页面无法快速定位需要关注的 issue。后端 priority 字段（Issue #24）和 mergeState 已就绪，前端卡片需要跟进以匹配后端能力。

## What Changes

- IssueCard 组件全面重设计：建立 8 层信息层次（Problem 信号 > Priority > Action Needed > Type > Title > 时间上下文 > Agent Running > Area Labels）
- 左侧色带按类型标签着色（bug=红, feature=绿, enhancement=蓝, tech-debt=灰, performance=黄）
- 条件 badge 叠加：Agent Running（蓝色脉冲）、Approval Waiting（amber）、Merge Conflict/Failed（红色）、Closed（灰色叠加）
- Priority（P0-P4）在卡片头部显示
- 时间上下文（相对时间 "2d ago"）在右下角灰色小字显示
- Label 颜色映射：类型标签着色药丸、critical 深红背景、区域标签灰色小药丸
- Done 列折叠：默认只显示最近 5 个 + "N more" 展开
- 前端 Issue type 添加 priority 字段
- 新增 label-colors.ts 工具模块和 relative-time.ts 工具模块
- 确认后端 API 响应已暴露 priority 字段（需重启 server）

## Capabilities

### New Capabilities

- `kanban-issue-card` — IssueCard 信息层次渲染：priority 显示、类型色带、条件 badge、label 颜色映射、相对时间
- `kanban-done-column` — Done 列折叠交互：默认折叠最近 5 个，点击展开全部

### Modified Capabilities

- `web-ui` — 新增 IssueCard 状态展示要求：卡片 SHALL 显示 priority、mergeState、相对时间、类型色带和条件 badge

## Impact

- **前端组件**：`IssueCard.tsx` 全面重写，`KanbanBoard.tsx` 添加 closed issue 处理，`StageColumn.tsx` 添加 Done 列折叠
- **前端类型**：`types.ts` Issue interface 添加 `priority` 字段
- **新增工具模块**：`label-colors.ts`、`relative-time.ts`
- **后端**：无数据模型变更，仅需确认 server 重启后 priority 字段在 API 响应中暴露
- **无新依赖**：纯 Tailwind + 原生 React 实现
- **无 API 变更**：不新增 endpoint，不改变数据模型
