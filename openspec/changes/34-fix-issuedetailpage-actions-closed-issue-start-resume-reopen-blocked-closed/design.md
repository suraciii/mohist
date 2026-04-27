## Context

前端 `IssueStatus` 枚举只有 4 个值（Active/Paused/Blocked/Interrupted），后端有 6 个（多了 Closed/Completed）。Actions 面板按钮以 `isDraft = (issue.stage === Stage.Draft)` 为条件，不检查 status，导致 Closed+Draft 的 issue 仍显示 Start 按钮。`IssueCard` 中 `issue.status === IssueStatus.Blocked` 显示 "Closed" 文字，语义错误。

三个文件需要改动：`types.ts`（枚举）、`IssueDetailPage.tsx`（Actions + badge）、`IssueCard.tsx`（badge）。纯前端变更，无后端改动。

## Goals / Non-Goals

**Goals:**
- 前端 IssueStatus 枚举与后端完全对齐
- Actions 面板按钮逻辑改为 Status 优先、Stage 修饰
- Blocked/Closed/Completed/Paused 各状态有正确的按钮和视觉
- IssueCard badge 语义正确区分

**Non-Goals:**
- 不改动后端 API 或数据模型
- 不实现 #20（Done 列视觉区分），仅确保 Completed 有基础终态标识，不冲突
- 不改变 Stage 进度条的显示逻辑

## Decisions

### D1: Actions 面板使用 Status-first switch 结构

将当前散落的条件判断替换为以 `issue.status` 为第一层判断的结构。在组件内计算一个 `actions` 变量，通过 switch/case 或 if-else chain 产出按钮配置数组，最后统一渲染。

```
switch (issue.status) {
  case Closed:      → [Reopen]
  case Completed:   → [] (显示提示文字)
  case Paused:      → [Resume, Close]
  case Blocked:     → [Reopen, Close]
  case Interrupted: → [Resume Pipeline, Close]
  case Active:
    if stage === Draft → [Start, Explore]
    else               → [Close] (+ Approve/SendMessage if approvalGate)
}
```

**Alternatives considered:** 提取为独立 hook `useIssueActions(issue)` — 过度抽象，当前只有一个消费者，直接在组件内处理即可。

### D2: statusBadge 新增两分支，移除 default

在现有 switch 中添加 `Closed` → 灰色、`Completed` → 绿色分支。移除 `default` 分支（或保留但加 console.warn），确保所有枚举值都有显式匹配。

**Alternatives considered:** 用 Record 映射替代 switch — 可行但当前只有 6 个值，switch 更清晰且与现有代码风格一致。

### D3: IssueCard badge 使用条件渲染替代 text 内容

当前 `IssueCard` 第 75 行 `issue.status === IssueStatus.Blocked` 显示文字 "Closed"，需要修正为：
- Blocked → 红色文字 "Blocked"
- Closed → 灰色文字 "Closed"（新增条件）
- Completed → 绿色勾号 + "Completed"（新增条件）
- Paused → 保持现有 "Paused"

每个状态独立的条件渲染块，不抽取公共组件。

### D4: resume 操作复用 reopenMutation

Paused 状态的 Resume 按钮调用 `api.reopenIssue()`，与 Blocked 的 Reopen 按钮共用同一个 `reopenMutation`。后端 reopen API 已支持 Paused 状态的恢复。

## Risks / Trade-offs

- [后端新增状态值导致前端再次不同步] → 枚举补齐后仍需人工维护对齐。可在 types.ts 中添加注释引用后端源文件路径。
- [Completed 状态终态标识与 #20 方案冲突] → 当前只做最小实现（绿色 badge + 提示文字），#20 负责更丰富的 Done 列区分。

## Migration Plan

无迁移。纯前端改动，部署后立即生效。Closed issue 的详情页从显示 Start 切换为显示 Reopen，不影响数据。

## Open Questions

无。
