## Context

The Issue Detail page has two disconnected regions: PipelineView (top) with an InlineApproval panel that only shows Approve/Send back buttons, and ChangesPanel (bottom) with the diff. When the pipeline pauses for approval, the user sees no review context — neither the self-review report (Plan stage) nor the AI code review report (Check stage). A fully-built ReviewApprovalPanel (483 lines) with result-dependent actions, dimension summaries, and a full-report modal already exists but is not wired into any page.

The root cause is two-fold: (1) `BaseStageRunner.handleAskUser()` writes `output: null` to `approvalState`, so the frontend has nothing to display, and (2) the InlineApproval component doesn't consume `approvalState.output` even if it were populated.

**Key constraint from product discussion**: The approval panel is a decision panel, not a reader. Review reports and diffs are separate concerns — no special file-type rendering, no diff embedding in the approval panel.

## Goals / Non-Goals

**Goals:**
- Populate `approvalState.output` with structured review data at the ask-user boundary
- Display review summary (verdict + dimension breakdown) inside InlineApproval
- Provide "View Full Report" (modal) and "View Changes" (scroll-to-diff) quick links
- Show result-dependent action buttons (PASS/FAIL/UNKNOWN → different button sets)

**Non-Goals:**
- New routes, pages, or API endpoints
- Special rendering for .md or any file type in the diff
- New diff rendering or syntax highlighting libraries
- Embedding the diff view inside the approval panel
- Reusing ReviewApprovalPanel as a whole (it couples rebase logic, sendMessage hooks, and a different page context)

## Decisions

### D1: Populate approvalState.output in handleAskUser via stage-specific extraction

The `handleAskUser` method receives `ctx` (which has `issue.stage`) and `allResults` (check results from the current stage execution). Use these to build a `ReviewOutput`-shaped object and write it as `output`.

**Why here**: `handleAskUser` is the single point where all ask-user reactions converge. Both Plan's SelfReviewPassedCheck and Check's UserApprovalCheck funnel through here. One insertion point covers all stages.

**Plan stage data source**: Read `self-review.md` from the change directory using `ctx.artifactManager.getChangeDir()` + `readReportFile()`. Parse verdict with existing `parseVerdict()` and dimensions with `parseDimensions()`.

**Check stage data source**: Extract the `ai-review` check result from `allResults`. Its `output` already contains `{ verdict, reviewReport, fixSuggestions }` (see `ai-review-check.ts:57-61`). Parse dimensions from `reviewReport` using `parseDimensions()`.

**Format** (matches frontend `ReviewOutput` type from `ReviewSummary.tsx`):
```ts
{
  result: 'PASS' | 'FAIL',
  dimensions: ParsedDimension[],
  reviewReport: string,       // Check: from ai-review output
  selfReviewNotes: string,    // Plan: full self-review.md content
}
```

**Alternatives considered:**
- *Override in each StageRunner*: Would require overriding handleAskUser in PlanStageRunner and CheckStageRunner separately, duplicating the ask-user flow. Rejected — more code, more divergence.
- *Let frontend assemble from multiple sources*: Would need new APIs for self-review.md access, and frontend would need to join approvalState + executions + file content. Rejected — adds complexity to the frontend and requires new endpoints.

### D2: Extract FullReportModal and ResultBadge from ReviewApprovalPanel into shared components

Move `FullReportModal` and `ResultBadge` out of `ReviewApprovalPanel.tsx` into a new file (e.g., `ReviewReportModal.tsx`). These are the only two reusable pieces — the rest of ReviewApprovalPanel couples rebase, sendMessage hooks, and layout that doesn't apply to InlineApproval.

**Why extract rather than import from ReviewApprovalPanel**: ReviewApprovalPanel imports `useLiveTask`, `useSendMessage`, and manages rebase state. Importing from it would pull in unnecessary dependencies. Clean extraction is ~120 lines.

**Alternatives considered:**
- *Inline the modal directly in PipelineView*: Duplicates existing working code. Rejected.
- *Reuse ReviewApprovalPanel as-is*: It has rebase UI, different mutation flows, and a different page layout contract. Rejected — too coupled.

### D3: Enhance InlineApproval in-place rather than replacing with ReviewApprovalPanel

InlineApproval is embedded deep inside `StepList` in PipelineView. Enhance it to:
1. Accept `approvalOutput` prop (from `issue.approvalState.output`)
2. Render `ReviewSummary` component above the action buttons
3. Add "View Full Report →" link (opens FullReportModal)
4. Add "View Changes ↓" link (calls `scrollIntoView` on ChangesPanel via a ref)
5. Switch action buttons based on `classifyResult(review.result)` — PASS/FAIL/UNKNOWN each get different button sets (mirroring ReviewApprovalPanel's logic)

**Why enhance rather than replace**: InlineApproval lives inside StepList, inside PipelineView. Its positioning and visibility logic are tightly coupled to the stage execution flow. Replacing it with ReviewApprovalPanel would require rewiring the entire StepList → InlineApproval relationship.

**scroll-to-diff mechanism**: Add an `id="changes-panel"` attribute to the ChangesPanel wrapper div in `IssueDetailPage.tsx`. The "View Changes ↓" link uses `document.getElementById('changes-panel')?.scrollIntoView({ behavior: 'smooth' })`.

**Alternatives considered:**
- *React context/ref for scroll target*: More complex, requires lifting refs. Rejected — a simple `id` attribute is sufficient.
- *Replace InlineApproval with ReviewApprovalPanel entirely*: Requires matching the ReviewApprovalPanel props interface (rebase, onViewFiles) inside PipelineView. Rejected — too much coupling.

### D4: Extend parseReviewOutput to normalize verdict → result

The Check stage's ai-review check stores `verdict` in its output, but the frontend `ReviewOutput` type expects `result`. Add a normalization step in `parseReviewOutput`: if `output.verdict` exists but `output.result` doesn't, map `verdict` → `result`.

**Why in parseReviewOutput**: This is the single parsing entry point. Both ReviewApprovalPanel and InlineApproval use it. One change covers all consumers.

## Risks / Trade-offs

- **[handleAskUser gains stage-aware logic]** → Mitigation: logic is a simple if/else on `ctx.issue.stage` with well-defined data extraction. No branching complexity.
- **[parseReviewOutput now handles an extra field alias]** → Mitigation: additive change, existing behavior unchanged. Fallback to UNKNOWN if neither field present.
- **[ReviewApprovalPanel.tsx may break if shared components are moved]** → Mitigation: ReviewApprovalPanel is not used by any page. It can safely import from the new shared file or remain unchanged. No consumers to break.
- **[approvalState.output size]** → Mitigation: review.md and self-review.md are typically 2-8KB. No truncation needed. If files are missing, output remains null and the UI gracefully degrades to current behavior.

## Migration Plan

1. **Backend**: Modify `handleAskUser` in `base-stage-runner.ts` — add review data extraction
2. **Frontend**: Extract `FullReportModal` + `ResultBadge` into shared component file
3. **Frontend**: Extend `parseReviewOutput` for `verdict` alias
4. **Frontend**: Enhance `InlineApproval` with review summary, modal link, scroll link, result-dependent buttons
5. **Frontend**: Add `id="changes-panel"` to ChangesPanel wrapper in `IssueDetailPage.tsx`

No database migration needed — `approvalState.output` is already typed as `unknown`/JSON column. No API changes needed — `approvalState` is already serialized in the issue response.

Rollback: Revert to `output: null` in handleAskUser and the InlineApproval gracefully shows the old button-only UI.

## Open Questions

None.
