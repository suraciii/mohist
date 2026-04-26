## Why

审批面板的 Approve 按钮依赖 `issue.comments`（`lastAgentComment`）才能显示，但 agent 的审查报告存储在 `approvalState.output` 中、不在 comments 里。当 issue 没有 comments 时，Approve 按钮完全消失，用户无法推进工作流——这曾导致多个 issue 卡在审批阶段形成死锁。同时，用户审批时看不到 agent 的审查报告（selfReviewNotes、reviewReport），无法做出明智的 approve/reject 决策。

## What Changes

- Approve 按钮显示条件从 `isApprovalGate && lastAgentComment` 改为 `isApprovalGate`（只要 approvalState.status === 'awaiting' 就显示）
- 审查报告内容从 `lastAgentComment.body`（comments）改为 `approvalState.output`，fallback 到 comments
- `ApprovalState` 类型增加 `output` 字段（`Record<string, unknown>` 或结构化类型），使前端能类型安全地访问审查报告数据

## Capabilities

### New Capabilities

- `approval-output-display` — 审批面板展示 `approvalState.output` 中的审查报告，而非依赖 comments

### Modified Capabilities

- `web-ui` — 审批面板行为变更：Approve 按钮不再依赖 comments，审查报告来源从 comments 切换到 approvalState.output

## Impact

- `packages/cli/web/src/components/IssueDetailPage.tsx` — 审批面板逻辑（Approve 按钮条件 + 报告展示）
- `packages/cli/web/src/lib/types.ts` — `ApprovalState` 接口增加 `output` 字段
- 无后端改动（后端已将报告存入 `approvalState.output`，只是前端未使用）
