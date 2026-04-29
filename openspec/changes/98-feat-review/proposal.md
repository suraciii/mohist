## Why

The review approval panel treats its job as "display a report" rather than "help a tech lead make a decision." When review FAILs, the user cannot see the conclusion at a glance (result badge is gray), PASS and FAIL dimensions carry equal visual weight, the full report pushes action buttons off-screen, and "Send back for fixes" ships the entire ~2000-word report to the agent instead of a focused issue summary. This forces a 30-second-to-minutes decision into a multi-minute scavenger hunt every time.

## What Changes

- Add a **Result Banner** — always-visible, color-coded PASS (green) / FAIL (red) / REVIEW (gray) header that shows the conclusion, pass ratio, and failing dimension names at a glance
- Add **Issue Summary** — FAIL dimensions expanded as issue cards with bullet-point descriptions; PASS dimensions collapsed to a single line
- Restructure the **Action Area** to be result-dependent: PASS shows a single "Approve & Done" button; FAIL shows "Send back for fixes" (primary), "Add instructions" (expandable textarea), and "Approve anyway" (secondary); REVIEW shows "Approve & Continue" + "Send back with notes"
- Rename "Force Approve" to "Approve anyway" and remove the double-click confirmation
- Replace in-panel full report expansion with a **Full Report Modal** (overlay, 80% width, Markdown rendered) triggered by a "View Report" link
- Optimize **Send Back** to extract and send only failing dimension issues (structured summary) instead of the entire `reviewReport`; fallback to "Fix Suggestions" section if dimension data is unavailable
- Extract review UI from the monolithic `IssueDetailPage.tsx` into dedicated `ReviewSummary.tsx` and `ReviewApprovalPanel.tsx` components

## Capabilities

### New Capabilities

- `review-decision-panel` — decision-oriented review approval UI with result banner, issue summary, result-dependent action area, and full report modal

### Modified Capabilities

None. The existing `web-ui` spec covers general UI patterns (SSE reactivity, question handling, project management) but does not define review-specific panel behavior. This change introduces a new capability without modifying existing spec-level requirements.

## Impact

- **Frontend components**: `IssueDetailPage.tsx` (refactor review/approval sections into new components), new `ReviewSummary.tsx` (Result Banner + Issue Summary), new `ReviewApprovalPanel.tsx` (Action Area + Full Report Modal)
- **No backend API changes**: existing `approve`, `sendMessage`, and `rebase` endpoints are sufficient; the "Send back for fixes" optimization is purely a frontend message-composition change
- **No type changes**: `ApprovalState.output` already contains `dimensions`, `result`, `selfReviewNotes`, and `reviewReport` — the structured data needed for the banner and issue summary is already in the response
