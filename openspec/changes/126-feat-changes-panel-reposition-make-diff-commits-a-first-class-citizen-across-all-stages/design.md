## Context

IssueDetailPage renders the Changes section (diff/commits viewer) as the last item in the main content column, gated by `DIFF_STAGES = new Set([Explore, Plan, Build, Check, Done])`. The data is already fetched unconditionally via `useIssueDiff` and `useIssueCommits` hooks (both `enabled: number > 0` with no stage check). The stage gate only controls rendering (`showDiff = DIFF_STAGES.has(issue.stage)` at line 265). This means the change is purely a JSX restructuring — no data fetching changes needed.

Current JSX order in the main column (`lg:col-span-2`):
1. `BranchBar` (line 346)
2. Description (line 347–352)
3. TaskList (line 354–364)
4. Comments + comment input (line 366–415)
5. Diff/Commits section (line 417–524, gated by `showDiff`)

Target order:
1. BranchBar
2. Description
3. **Changes** (with summary stats header, always visible)
4. TaskList
5. Comments + comment input

## Goals / Non-Goals

**Goals:**
- Remove `DIFF_STAGES` gate so Changes renders in all stages
- Move Changes section from after-Comments to after-Description
- Add summary statistics header (file count, +/- lines, commit count)
- Show "No changes yet" empty state when no data
- Add compact changes summary to PlanApprovalPanel and ReviewApprovalPanel

**Non-Goals:**
- No new API endpoints or data fetching changes
- No changes to DiffViewer, CommitRow, or diff rendering logic
- No commit comparison or diff algorithm changes

## Decisions

### D1: Extract ChangesPanel as a standalone component

Move the inline JSX block (lines 417–524) into a dedicated `ChangesPanel` component. This reduces IssueDetailPage complexity and makes the summary stats header self-contained.

**Props**: `issueNumber`, `diffData`, `commitsData`, `diffTab`, `setDiffTab`, `expandedFiles`, `setExpandedFiles`, `expandedCommits`, `setExpandedCommits`

**Alternatives considered:**
- Keep inline in IssueDetailPage — would work but the block is ~100 lines and will grow with the summary header, making the already large component harder to navigate
- Lift diff/commits state into the component — would require moving hooks and breaking the existing `CommitRow` pattern that uses `useCommitDiff` internally

### D2: Summary statistics computed from existing API responses

Compute summary stats in `ChangesPanel` from `diffData.files` and `commitsData.commits`:
- File count: `files.length`
- Additions/deletions: sum of `file.additions` / `file.deletions` across all files
- Commit count: `commits.length`

No new API call needed. The data is already fetched.

**Alternatives considered:**
- Dedicated backend summary endpoint — over-engineering for data already on the client
- Compute from commits API `--stat` output — diff API already has precise `--numstat` numbers

### D3: Empty state shown via a simple conditional

When `files.length === 0 && commits.length === 0`, render a compact card with "No changes yet" text instead of the full panel with tabs. This avoids showing empty tabs UI.

### D4: Changes summary in approval panels passed as props

Add `changesSummary?: string` prop to both `PlanApprovalPanel` and `ReviewApprovalPanel`. IssueDetailPage computes the summary string and passes it down. The panels render it as a compact one-liner above the action buttons.

This avoids coupling the approval panels to diff/commits hooks.

**Alternatives considered:**
- Approval panels fetch data independently — duplicate fetches, unnecessary coupling
- Context/provider pattern — over-engineering for a single string

## Risks / Trade-offs

- [Backlog/early Explore stages make API calls that return empty] → Acceptable: hooks already fetch unconditionally (`enabled: number > 0`), no change in network behavior
- [Summary stats may be briefly stale during active agent work] → Acceptable: existing refetch intervals handle this, summary updates on next refetch
- [Moving section may cause visual jump for users accustomed to old position] → Low risk: the change improves discoverability, which outweighs muscle memory

## Migration Plan

Single deploy — purely frontend JSX restructuring. No API changes, no database changes, no config changes. Rollback by reverting the commit.

## Open Questions

None.
