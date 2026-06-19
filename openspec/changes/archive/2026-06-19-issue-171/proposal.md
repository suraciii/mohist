## Why

The Epics page fails its core job: telling the user which epic to manage now, what's running inside it, and what to do next. Within each status group, cards sort by creation time instead of priority, so a P0 epic lands below a P2; the "Next" label points at the first incomplete issue by insertion order — ignoring priority, dependencies, and whether it can actually start; and the detail page "Current Activity" magnet always reads "0 blocked, 0 active" because progress counting compares an issue's `Status` enum against `Health`-field strings. Two presentation defects compound this: epic descriptions are rendered as pre-wrapped plain text (raw `##` / list markers leak through), and "Ready to mark done" shows on already-Done/Closed epics. The board is unusable for triage; this fixes the read model and the card/detail surface together.

## What Changes

- Epic list groups (Active / Done / Closed) are sorted within group by priority ascending (P0 → P4), then `updatedAt` descending; sorting moves server-side so the frontend doesn't re-sort.
- Done and Closed groups collapse by default (expandable); Active stays expanded.
- Fix progress counting in `EpicProgress`: derive active/blocked from issue `Health` (not `Status`), so counts are non-zero when real work exists. `EpicProgress.Build` stays a pure function and `EpicGrain.IsReadyToMarkDoneAsync` behavior is preserved.
- Upgrade `nextIssue`: select the highest-priority issue that is currently startable (`CanStart`, no undelivered `Blocker`) instead of the first incomplete-by-insertion-order; when none is startable, return a textual reason (e.g. "waiting on #N") rather than a misleading pick.
- Enrich the `activeIssues` / `blockedIssues` DTOs from id-only lists to `{id, number, title, health}` so the UI can show specific issues, not just counts.
- Epic list cards show both "In progress" and "Next" (gracefully degrading when either is empty); the detail page "Current Activity" magnet lists the concrete in-flight issues (colored by health, with navigation) instead of bare counts.
- Card status text branches by epic's own status: Active shows Next/Ready; Done shows a completion phrase; Closed shows a closed phrase. "Ready to mark done" no longer appears on Done/Closed epics.
- Epic detail description is rendered through the existing shared `MarkdownReader` instead of a `whitespace-pre-wrap` `<p>`, so `##`, lists, and emphasis render properly.

## Capabilities

### New Capabilities

- `epic-board`: Epic list and detail presentation surface — group collapse/expand behavior (Done/Closed collapsed by default), status-conditional card text (Active/Done/Closed branching, no stale "Ready to mark done"), the "In progress" + "Next" dual display on cards, the detail-page "Current Activity" listing of concrete in-flight issues with health color and navigation, and Markdown rendering of the epic description.

### Modified Capabilities

- `epic-tracking`: Epic read-model contract changes — server-side list ordering within status group by priority then `updatedAt`; projected progress counts (`activeIssues` / `blockedIssues`) computed from issue `Health` rather than `Status` strings; `nextIssue` selection by priority + startability (`CanStart` / `Blocker`) with a textual fallback when nothing is startable; enriched `activeIssues` / `blockedIssues` shape carrying `{id, number, title, health}`. The "Next issue recommendation" scenario and progress-counting behavior are superseded; `IsReadyToMarkDone` semantics are preserved.

## Impact

- **Read model / API**: `EpicQuerier.ListAsync` (sort by priority + `updatedAt`), `EpicQuerier.GetLinkedIssuesAsync` (read `CanStart` / `Blocker` from `IssueInfo`, extend `LinkedIssueDto`), `EpicProgress.Build` (counting by `Health`, `nextIssue` selection, enriched output shape).
- **Grain**: `EpicGrain.IsReadyToMarkDoneAsync` continues to call `EpicProgress.Build`; change must keep its return contract compatible so mark-done judgment does not regress.
- **DTOs**: `LinkedIssueDto` gains startability + identity/title/health fields; epic progress response gains structured active/blocked entries.
- **Frontend**: `EpicListPage.tsx` (group collapse, priority-ordered rendering consumption, status-branched card text, in-progress + next display), `EpicDetailPage.tsx` (description via `MarkdownReader`, "Current Activity" listing). Reuses `shared/ui/markdown-reader/MarkdownReader` — no new component.
- **Consumed, not changed**: `issue-start-readiness` (`CanStart` / `Blocker`) and `markdown-reader` are consumed as-is; their specs are not modified.
- **Risk**: medium — `EpicProgress` is a pure function reused by mark-done logic; the counting fix and `nextIssue` change must not alter `IsReadyToMarkDone` outcomes.
