## Context

IssueDetailPage renders a two-column layout (main 2/3 + sidebar 1/3). The diff/commits section currently lives at the bottom of the main column (after Comments), gated by `DIFF_STAGES = {Explore, Plan, Build, Check, Done}`. The `useIssueDiff` and `useIssueCommits` hooks already fetch data unconditionally (no stage-based `enabled` flag), so the data is always available — only the rendering is gated.

The diff section JSX (lines 417–525 in `IssueDetailPage.tsx`) is a single inline block with Files/Commits tabs and expandable diff. It uses `diffData?.files` (type `DiffFile[]`) and `commitsData?.commits` (type `CommitEntry[]`), both already fetched at the component top level (lines 137, 162).

The approval UI in IssueDetailPage's sidebar is rendered inline (not via separate panel components). `PlanApprovalPanel` and `ReviewApprovalPanel` components exist but are not imported — the approval gates are generic inline JSX blocks controlled by `isApprovalGate`.

## Goals / Non-Goals

**Goals:**
- Remove `DIFF_STAGES` gate so Changes renders in all stages
- Move Changes section from after-Comments to after-Description (before TaskList)
- Add summary statistics line (file count, +/- lines, commit count) above the tabs
- Show empty state for stages with no changes
- (Optional) Compact changes summary in sidebar approval panels

**Non-Goals:**
- No new API endpoints — reuse `getIssueDiff` / `getIssueCommits`
- No changes to DiffViewer component
- No commit comparison feature
- No changes to how `useIssueDiff` / `useIssueCommits` fetch data (they already run unconditionally)

## Decisions

### D1: Extract Changes section into its own component

The diff/commits JSX block (~110 lines) will be extracted into a `ChangesPanel` component. This keeps `IssueDetailPage.tsx` manageable and makes the summary header, empty state, and tab logic self-contained.

**Props:** `files: DiffFile[]`, `commits: CommitEntry[]`, `diffTab` / `setDiffTab`, `expandedFiles` / `setExpandedFiles`, `expandedCommits` / `setExpandedCommits`, `onCommitExpand`.

**Alternatives considered:**
- Keep inline in IssueDetailPage — already 851 lines, adding summary logic makes it harder to follow
- Make it a pure render component with no state — would require lifting more state up, not worth it for a single concern

### D2: Summary statistics computed from existing data

The summary line ("N files, +X/-Y, M commits") will be computed inline from `diffData.files` and `commitsData.commits` using `reduce`. No new data fetching needed — `DiffFile` already has `additions`/`deletions`, `CommitEntry` has `filesChanged`/`additions`/`deletions`.

**Alternatives considered:**
- Add a server-side summary endpoint — overkill; the data is already on the client
- Compute from commits rather than files — files array gives accurate per-file counts directly

### D3: Add changes summary to inline approval UI in IssueDetailPage

Note: `PlanApprovalPanel` and `ReviewApprovalPanel` components exist but are NOT imported/used in IssueDetailPage. The approval UI is rendered inline (lines 746–824) as generic approval gates. The changes summary will be added directly into the inline approval sections in IssueDetailPage's sidebar, above the "Approve & Continue" / "Approve & Build" buttons.

A compact summary block (file count, +/- lines, commit count) will be computed from `diffData`/`commitsData` and rendered inside the approval gate sections when `isApprovalGate` is true.

**Alternatives considered:**
- Add `changesSummary` prop to orphaned PlanApprovalPanel/ReviewApprovalPanel — these components aren't used in IssueDetailPage, so this wouldn't deliver value
- Use React context — overkill for a single numeric summary used in one component

### D4: Always render ChangesPanel (no early-return null)

Remove the `if (files.length === 0 && commits.length === 0) return null` guard. Instead, the panel always renders — showing the summary (with zeros) and the empty state message "No changes yet" when there's nothing.

**Alternatives considered:**
- Conditionally render with a wrapper check — the old pattern; defeats the goal of always-visible changes
- Hide the entire panel when empty — contradicts the Backlog "No changes yet" requirement

## Risks / Trade-offs

- [Backlog stage will trigger diff/commits API calls that return empty] → Already happens because `useIssueDiff`/`useIssueCommits` have `enabled: number > 0` with no stage filter. No new API load.
- [Moving a large UI block may introduce layout regressions] → The ChangesPanel component uses the same `rounded-lg border border-gray-200 bg-white p-4` card style as the surrounding sections. Visual diff testing recommended.
- [Extra inline summary in approval sections adds complexity] → The summary is a simple computed block (~5 lines of JSX), rendered conditionally when `isApprovalGate` is true. Low risk.

## Migration Plan

1. Extract `ChangesPanel` component from inline JSX
2. Remove `DIFF_STAGES` constant and `showDiff` variable
3. Place `<ChangesPanel>` after Description, before TaskList
4. Remove old diff section (after Comments)
5. Compute `changesSummary` and render inline in approval gate sections in IssueDetailPage sidebar
6. Build and visually verify all stages

No rollback strategy needed — this is a pure frontend layout change with no data migration.

## Open Questions

- Should the summary line be clickable (e.g., to scroll to or expand the panel)? → Start with static text; interactivity can be added later if users request it.
