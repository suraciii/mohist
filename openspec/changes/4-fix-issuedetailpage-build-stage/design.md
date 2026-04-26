## Context

`IssueDetailPage.tsx:15` 和 `IssueCard.tsx:4` 各自硬编码了 `APPROVAL_STAGES = new Set([Stage.Build])`，用于决定审批按钮/标记的显示。后端已经在所有需要审批的阶段（plan、build 等）设置了 `approvalState.status = "awaiting"`，但前端只看 stage 是否为 Build，导致 plan 阶段等审批时 UI 上无 approve 按钮。

`Issue` 类型已包含 `approvalState?: ApprovalState` 字段（`types/index.ts:61`），后端 API 也已正确返回该数据，无需后端改动。

## Goals / Non-Goals

**Goals:**
- 两个组件的审批判断统一改为 `approvalState.status === "awaiting"`
- 移除所有 `APPROVAL_STAGES` 硬编码

**Non-Goals:**
- 不修改后端 API 或类型定义
- 不改变审批流程本身的行为（approve/reject 逻辑不变）
- 不新增 stage 或修改 stage 定义

## Decisions

### D1: 用 `approvalState.status` 替代 `APPROVAL_STAGES` stage 白名单

直接检查 `issue.approvalState?.status === "awaiting"` 作为审批 UI 的唯一条件。后端已经正确管理 `approvalState` 的生命周期，前端无需重复用 stage 推断。

**具体改动：**

1. **`IssueDetailPage.tsx`** — 删除 L15 `APPROVAL_STAGES`，修改 L113-116 `isApprovalGate`：
   ```ts
   const isApprovalGate =
     issue.approvalState?.status === 'awaiting' &&
     issue.status === IssueStatus.Active &&
     !isAgentRunningOnThis
   ```

2. **`IssueCard.tsx`** — 删除 L4 `APPROVAL_STAGES`，修改 L13 `isApprovalGate`：
   ```ts
   const isApprovalGate = issue.approvalState?.status === 'awaiting' && issue.status === IssueStatus.Active
   ```

**Alternatives considered:** 扩展 `APPROVAL_STAGES` 白名单加入 `Stage.Plan` — 但这需要每次新增审批阶段时同步维护白名单，是脆弱的设计。用 `approvalState` 是后端已有的单一数据源。

## Risks / Trade-offs

- [approvalState 数据延迟或缺失] → 后端仅在明确需要审批时设置 `approvalState`，如果 API 返回不完整，审批按钮不会误显示（optional chaining 安全回退到 false）
- [旧 issue 没有 approvalState 字段] → 这些 issue 也不处于审批状态，optional chaining 处理为 undefined → 不显示按钮，行为正确

## Migration Plan

纯前端改动，无需数据库迁移或 API 变更。部署后立即生效。
