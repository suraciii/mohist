## Why

Review/approval UI treats every gate identically — a flat text box with a single "Approve & Continue" button. The review verdict (PASS/FAIL) is buried in raw markdown, there is no reject action despite backend support, and the plan-stage gate shows no design artifacts. Users cannot make informed decisions or send work back for fixes, making the approval flow a rubber stamp instead of a meaningful checkpoint.

## What Changes

- Add structured verdict badge (PASS/FAIL) as the primary visual element in review panels
- Parse and store verdict + per-dimension status in `approvalState.output` alongside the existing `reviewReport`
- Render stage-differentiated approval panels: plan stage shows design artifact previews, review stage shows structured verdict + dimension breakdown
- Add reject button with two modes: "Send back for fixes" (auto-includes review report) and "Send back with instructions" (user adds custom guidance)
- Render markdown in report details with expand/collapse instead of raw text scrollbox
- Wire frontend reject action to existing backend `POST /api/issues/:number/messages` (inject rejection message + review report into paused agent session)

## Capabilities

### New Capabilities

- `review-summary-ui` — Structured review panel with verdict badge, dimension status grid, and expandable full report
- `stage-differentiated-approval` — Plan-stage artifact preview panel and review-stage verdict panel with stage-appropriate actions
- `reject-and-fix` — Reject workflow: send-back-for-fixes (auto review report) and send-back-with-instructions (user message + review report)

### Modified Capabilities

- `approval-output-display` — Extend `approvalState.output` schema to include parsed `verdict` and `dimensions` fields; update display requirements to consume structured data
- `web-ui` — Add reject action to approval panel; remove single "Approve & Continue" in favor of verdict-aware actions

## Impact

- `packages/cli/web/src/components/IssueDetailPage.tsx` — Approval panel refactor (biggest change)
- `packages/cli/web/src/lib/types.ts` — ApprovalState.output type extension
- `packages/cli/src/types/index.ts` — Backend ApprovalState.output type extension
- `packages/cli/src/workflow/workflow-controller.ts` — Store parsed verdict + dimensions into approvalState.output
- `packages/cli/src/api/issues.ts` — No new endpoint needed; reject uses existing messages endpoint
- `packages/cli/web/src/lib/api.ts` — Reject helper using existing messages API
