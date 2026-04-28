## Context

Kanban IssueCard 是 `packages/cli/web/src/components/IssueCard.tsx`，当前只展示 3/13 个后端字段（number、title、labels），所有 labels 统一渲染为灰色药丸。后端 Issue 类型（`packages/cli/src/types/index.ts`）已有 `priority: Priority` 字段，但前端 `types.ts` 未包含该字段，API 响应中的 priority 值被忽略。

关键现状：
- `APPROVAL_STAGES` 只有 `Build`，缺少 Plan/Review（`IssueCard.tsx:6`）
- 无 label 颜色映射工具，无相对时间格式化工具
- `StageColumn.tsx` 无折叠逻辑，Done 列全部展开
- `mergeState` 字段在卡片上完全不展示
- 纯 Tailwind + React 技术栈，使用 @tanstack/react-query

前端源码位于 `packages/cli/web/src/`，使用 Vite 构建。

## Goals / Non-Goals

**Goals:**
- 将 IssueCard 信息密度从 23% 提升到覆盖 priority、mergeState、相对时间、类型色带等关键字段
- 建立 8 层视觉信息层次，让用户一眼扫到问题信号和优先级
- Done 列折叠，减少滚动噪音
- 修复 APPROVAL_STAGES 遗漏 Plan/Review 的问题
- 新增可复用的 label-colors 和 relative-time 工具模块

**Non-Goals:**
- 不改变后端数据模型或新增 API endpoint
- 不引入新依赖（纯 Tailwind + 原生 React）
- 不做拖拽排序
- 不改变卡片点击行为
- 不做虚拟列表或性能优化

## Decisions

### D1: 色带使用 Tailwind border-left 实现

左侧 4px 类型色带使用 `border-l-4` + 动态 `border-l-[color]` class 实现，而非嵌套一个额外的 div。这减少了一层 DOM 嵌套，且 Tailwind 的 border-l-4 已有良好浏览器兼容性。

颜色通过 inline style `style={{ borderLeftColor: getStripColor(labels) }}` 设置，因为颜色值来自运行时计算（标签映射），不适合用 Tailwind 静态 class。

**Alternatives considered:**
- 绝对定位 div 色带 → 多一层 DOM，需要 relative 容器
- CSS `::before` 伪元素 → React 中不便于动态颜色

### D2: Badge 使用条件优先级取最高一个

卡片右上角只显示优先级最高的单个 badge（Merge Conflict > Closed > Approval > Running），而非叠加显示多个 badge。原因：
- 卡片空间有限，多个 badge 会挤压标题区域
- 最高优先级信号已足够指导用户行为
- 闭合叠加层（Closed）和 badge 是互斥的——闭合后不会有 Running/Approval

**Alternatives considered:**
- 显示所有符合条件的 badge → 视觉噪音，卡片变高
- 仅显示 badge 图标无文字 → 不够直观

### D3: APPROVAL_STAGES 扩展为 Plan + Build + Review

当前 `APPROVAL_STAGES` 只包含 `Build`。扩展为 `new Set([Stage.Plan, Stage.Build, Stage.Review])`，匹配后端工作流的三个审批阶段。这是一个已知 bug，在此变更中一并修复。

### D4: Label 排序策略——类型标签优先，区域标签在后

卡片底部 label 行的渲染顺序：类型标签（bug/feature/enhancement）→ 紧急度标签（critical）→ 区域标签（agent/webui/api）→ 其他标签。同类别内按字母排序。这样视觉上重要的类型标签始终在左侧可见区域。

### D5: relative-time 使用纯计算函数，不引入 dayjs/date-fns

相对时间格式化逻辑简单（5 个 if/else 分支），使用原生 `Date` 差值计算即可。不引入 dayjs 或 date-fns 等库，符合"不引入新依赖"的约束。

### D6: Done 列折叠状态用 React useState 管理

Done 列的展开/折叠状态用 `useState(false)` 管理在 `StageColumn` 组件内部。不持久化到 URL query params 或 localStorage——这是纯 UI 交互状态，页面刷新回到默认折叠即可。

### D7: Closed issue 渲染为灰色叠加而非过滤隐藏

Closed issue（`status === 'blocked'`）在看板中显示但降低视觉权重：整体卡片加 `opacity-50` + "Closed" 覆盖文字。不过滤隐藏，因为用户需要看到所有 issue 的状态。

**Alternatives considered:**
- 完全过滤掉 closed issue → 用户可能忘记这些 issue 存在
- 移到单独列 → 增加列数量，看板过宽

## Risks / Trade-offs

- **[Priority 值格式不匹配]** 后端 priority 是 `'p0'|'p1'|...` 字符串，spec 用 P0-P4 数字 → 需在前端做格式转换 `parseInt(priority.slice(1))` 或统一用字符串显示。**Mitigation**: `label-colors.ts` 中提供 `formatPriority(priority: string): string` 工具函数。
- **[mergeState 值可能多样]** 后端 mergeState 有 6 种值（pending/merging/merged/build-failed/conflict/null），badge 文本需要映射。**Mitigation**: 在 badge 渲染中使用映射表 `{ 'build-failed': 'Failed', 'conflict': 'Conflict', 'pending': 'Pending', 'merging': 'Merging' }`。
- **[Labels overflow 截断]** 卡片底部标签多时会换行或溢出。**Mitigation**: 使用 `overflow-hidden` + `text-ellipsis`，限制标签行为一行显示，超出截断。
- **[inline style 与 Tailwind JIT]** `style={{ borderLeftColor }}` 不走 Tailwind purge，但这是单属性 inline style，对 bundle 大小无影响。

## Migration Plan

1. **先添加工具模块**：`label-colors.ts` 和 `relative-time.ts`（无副作用，可独立测试）
2. **更新 types.ts**：Issue interface 添加 `priority` 字段
3. **重写 IssueCard.tsx**：替换整个组件
4. **更新 StageColumn.tsx**：Done 列折叠逻辑
5. **确认后端**：重启 server 确保 priority 在 API 响应中暴露
6. **视觉验证**：检查所有 badge 状态、色带颜色、标签样式
7. **无 rollback 风险**：纯前端变更，无数据库迁移，revert 即可

## Open Questions

- 后端 priority 字段在 API 响应中的确切格式：是 `'p0'` 字符串还是 `0` 数字？需确认后适配前端 type。从后端类型定义看是 `'p0'|'p1'|...` 字符串格式。
