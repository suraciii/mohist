## Context

审批面板当前有两层逻辑耦合在一个条件块中：

1. **Approve 按钮**（`IssueDetailPage.tsx:385-418`）：包裹在 `{isApprovalGate && lastAgentComment && ...}` 中。`lastAgentComment` 取自 `issue.comments`，但 agent 的审查报告存储在 `approvalState.output` 而非 comments。当 issue 无 comments 时按钮消失，形成死锁。

2. **审查报告展示**（`IssueDetailPage.tsx:394-398`）：直接展示 `lastAgentComment.body`，看不到 `approvalState.output` 中的 `selfReviewNotes` 或 `reviewReport`。

后端 `ApprovalState` 已有 `output: unknown` 字段（`packages/cli/src/types/index.ts:45`），前端 `ApprovalState`（`packages/cli/web/src/lib/types.ts:15-19`）缺少此字段。后端在 Plan 阶段存 `{ stage, issueNumber, selfReviewNotes }`，在 Review 阶段存 `{ stage, issueNumber, reviewReport }`。

## Goals / Non-Goals

**Goals:**
- Approve 按钮只依赖 `isApprovalGate`，不依赖 comments
- 审查报告从 `approvalState.output` 读取展示，fallback 到 comments
- 前端 `ApprovalState` 类型与后端对齐

**Non-Goals:**
- 不修改后端逻辑（output 字段已存在且数据已写入）
- 不修改 output 的数据结构（保持 `{ selfReviewNotes }` / `{ reviewReport }` 现有格式）
- 不做结构化 markdown 渲染（`selfReviewNotes` / `reviewReport` 本身是 markdown 文本，用 `whitespace-pre-wrap` 展示即可）

## Decisions

### D1: output 字段类型使用 `Record<string, unknown>`

前端 `ApprovalState.output` 声明为 `Record<string, unknown>`，而非精确匹配后端的 Plan/Review 输出结构。后端 `output` 类型就是 `unknown`，不同 stage 的 output 结构不同，前端只需读取并展示文本字段。

**Alternatives considered:** 定义 union type `PlanOutput | ReviewOutput` — 过度设计，当前只需要取文本值展示。

### D2: 报告内容提取逻辑放在组件内

在 `IssueDetailPage` 组件中用一行计算 `reviewOutput`：

```ts
const reviewOutput = issue.approvalState?.output
  ? (issue.approvalState.output as Record<string, unknown>).selfReviewNotes
    || (issue.approvalState.output as Record<string, unknown>).reviewReport
    || JSON.stringify(issue.approvalState.output, null, 2)
  : lastAgentComment?.body
```

优先取 `selfReviewNotes`（Plan stage），其次 `reviewReport`（Review stage），最后 fallback 到 comments。不用提取为独立 hook — 逻辑足够简单且仅此一处使用。

**Alternatives considered:** 提取 `useReviewOutput(issue)` hook — 单一使用点，不必要。

### D3: 拆分审批面板为两个独立块

当前是一个 `{isApprovalGate && lastAgentComment && ...}` 块同时包含报告和按钮。改为：

1. **报告区域**：`{isApprovalGate && reviewOutput && <报告面板>}` — 有内容才显示
2. **Approve 按钮**：`{isApprovalGate && <按钮面板>}` — 始终显示

两个块各自独立，不再嵌套。Send Message 块保持不变（已经独立，只依赖 `isApprovalGate`）。

**Alternatives considered:** 保持单一条件块 `{isApprovalGate && ...}` 内部处理空报告 — 逻辑更复杂，不如拆分清晰。

### D4: lastAgentComment 计算简化

移除 `lastAgentComment` 变量。当前它仅在审批面板的 fallback 路径和报告展示中使用。改为在 `reviewOutput` 计算中直接内联 fallback 逻辑。如果 `reviewOutput` 最终 fallback 到 comments，从排序后的 `comments` 数组取最后一条。

## Risks / Trade-offs

- **[Risk] `output` 可能为空对象 `{}`（truthy 但无实际内容）]** → 在 `reviewOutput` 计算中检查提取到的值是否为 truthy string，空对象时 fallback
- **[Risk] Plan stage output 有 `selfReviewNotes`，Review stage output 有 `reviewReport`，未来可能有新字段]** → 用 `||` 级联覆盖，最后加 `JSON.stringify` 兜底

## Migration Plan

纯前端改动，无需数据迁移。后端数据结构不变。部署后审批面板立即使用 `approvalState.output`，已有 issue 的 `output` 字段已由后端写入，无需额外处理。

## Open Questions

无。
