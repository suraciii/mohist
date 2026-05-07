## Why

The InlineApproval panel on the Issue Detail page is a blank decision point — it shows only Approve/Send back buttons with no review context. Users must manually scroll to the diff to find review reports, and even then the self-review (Plan stage) and AI review (Check stage) reports are invisible in the UI. The existing ReviewApprovalPanel (483 lines, with result-dependent actions and report modals) is already built but unused.

## What Changes

- Fix `handleAskUser` in `base-stage-runner.ts` to populate `approvalState.output` with structured review data instead of `null`
- Plan stage: parse self-review.md → extract verdict + dimensions → write to approvalState.output
- Check stage: extract AI review check result (verdict, report, dimensions) from StageExecution.checkResults → write to approvalState.output
- Extend `parseReviewOutput` in `ReviewSummary.tsx` to handle `verdict` field alias (Check stage uses `verdict` not `result`)
- Enhance `InlineApproval` in `PipelineView.tsx` to display review summary (reuse `ReviewSummary` component) and add "View Full Report" and "View Changes" links
- Extract reusable UI from `ReviewApprovalPanel.tsx` — `FullReportModal`, `ResultBadge`, and result-dependent action buttons — for use in `InlineApproval`

## Capabilities

### New Capabilities

- `approval-output-data`: Backend populates `approvalState.output` with structured review data (verdict, dimensions, report) at the ask-user boundary, for both Plan and Check stages

### Modified Capabilities

- `web-ui`: InlineApproval panel enhanced to show review summary, full report modal, and scroll-to-changes link; parseReviewOutput extended for verdict field

## Impact

- **Backend**: `base-stage-runner.ts` (handleAskUser), `plan-stage-runner.ts` (self-review parsing), `check-stage-runner.ts` (AI review extraction), `utils.ts` (existing parseVerdict/parseDimensions reused)
- **Frontend**: `PipelineView.tsx` (InlineApproval), `ReviewSummary.tsx` (parseReviewOutput), `ReviewApprovalPanel.tsx` (extract shared components)
- **Types**: `ApprovalState.output` already typed as `unknown` — no schema change needed, just populated with `ReviewOutput`-shaped data
- **No new dependencies**: Reuses existing `react-markdown` (already in ReviewApprovalPanel), existing UI patterns
- **Existing specs**: `web-ui`, `pipeline-model`, `ask-user-tool` — only `web-ui` needs a delta spec for InlineApproval behavior
