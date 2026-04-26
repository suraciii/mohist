## Why

IssueDetailPage 和 IssueCard 中的审批按钮显示条件硬编码为 `Stage.Build`，导致 plan 阶段等审批时页面上没有 approve 按钮，用户无法通过 Web UI 批准 plan 阶段。这是一个功能性阻断 bug，应改为检查 `issue.approvalState?.status === "awaiting"` 来驱动审批 UI。

## What Changes

- 移除 `IssueDetailPage.tsx` 和 `IssueCard.tsx` 中硬编码的 `APPROVAL_STAGES = new Set([Stage.Build])`
- 将审批按钮显示条件从 stage 检查改为 `approvalState.status === "awaiting"` 检查
- IssueCard 的审批状态指示器同步修改为基于 `approvalState`

## Capabilities

### New Capabilities

### Modified Capabilities

- `web-ui` — 审批按钮的显示逻辑从 stage 硬编码改为 approvalState 驱动

## Impact

- `packages/cli/web/src/components/IssueDetailPage.tsx` — 移除 `APPROVAL_STAGES`，修改 `isApprovalGate` 条件
- `packages/cli/web/src/components/IssueCard.tsx` — 移除 `APPROVAL_STAGES`，修改审批状态判断
