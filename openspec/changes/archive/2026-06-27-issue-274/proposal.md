## Why

The Epics list page (`EpicListPage`) is slow because `EpicQuerier.ListAsync` (`EpicQuerier.cs:21`) loops over the N fetched epics and, for each one, re-runs the heavy `IssueQuerier.ListAsync(projectId, all: true)` (`EpicQuerier.cs:98`). That full-enrichment call returns identical project-wide data every iteration yet costs ~11 SQL queries each — including two large `WorkflowRuns.State` JSON deserializations, Comments, Attachments, and agent config. A 10-epic board fires 100+ redundant SQL queries that enrich data the list view's progress computation never needs. This must be fixed now because epic boards are the primary entry point for issue lifecycle work, and the cost grows linearly with epic count.

## What Changes

- Add four **stored computed columns** to the `Issues` table — `Title`, `Priority`, `IsDraft`, `PrerequisiteNumbersJson` — each sourced from `json_extract(State, '$.…')` with `COALESCE` to tolerate camelCase/PascalCase, mirroring the existing `ProjectId`/`Number`/`Status`/`IsArchived` mechanism. Stored computed columns auto-sync with the State JSON, so there is no new write path and no consistency maintenance.
- Rewrite `EpicQuerier.ListAsync` into a **single aggregate SQL** (`Epics` ⋈ `EpicIssues` ⋈ `Issues`) that selects only the columns the list-page progress needs, then groups in memory and feeds the unchanged pure function `EpicProgress.Build`.
- Eliminate the list page's dependence on deserializing `WorkflowRuns.State` JSON and on reading Comments/Attachments/agent-config — none of which affect progress, `readyToMarkDone`, or `nextIssue`.
- Compute `nextIssue` / `CanStart` from the new `PrerequisiteNumbersJson` + `IsDraft` columns combined with the joined issues' stored `Status`, instead of loading full workflow state.
- **Health approximation (list view only)**: `in_progress` linked issues are treated as `active` rather than being split into `blocked`/`active`, since `EpicListPage.tsx:60` only consumes `progress.activeIssues[0]` and never `blockedIssues`. Precise Health continues to be served by the epic **detail** page (`EpicDetailDto`, single epic, full path unchanged).
- `EpicWithProgressDto` shape, the HTTP API contract, and the web client are unchanged (frontend is unaware).

No breaking changes.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

_None._ The change is performance-only. The `EpicWithProgressDto` shape, the HTTP API contract, the frontend behavior, and the `EpicProgress.Build` ordering/readiness computation (governing `epic-lifecycle` requirements such as "Next-issue selection ordering") are all preserved. The list-page Health approximation is an implementation-level tradeoff — invisible to the current frontend, which reads only `activeIssues[0]` — and is not currently governed by any spec requirement; precise Health remains on the epic detail page.

## Impact

- **Data / Schema** (`packages/server`): one EF Core migration adding the four stored computed columns to `Issues` (`Title`, `Priority`, `IsDraft`, `PrerequisiteNumbersJson`) via `COALESCE(json_extract(...))`, following the pattern in `AddIssueIsArchivedComputedColumn`.
- **Query layer** (`packages/server`): `EpicQuerier.ListAsync` rewritten to one aggregate SQL; the list path stops calling `IssueQuerier.ListAsync` and stops deserializing `WorkflowRuns.State`. `IssueQuerier` itself is unchanged.
- **Detail path**: `EpicQuerier.GetAsync` / `GetByNumberAsync` (`EpicDetailDto`) keep the full-enrichment path — single epic, no performance concern.
- **Web** (`packages/web`): no change required.
- **Tests**: server tests for the rewritten `EpicQuerier.ListAsync` (progress accuracy, `nextIssue`/`CanStart`, ordering, empty epic, archived issues) and the computed-column migration.
- **Risk**: a just-blocked linked issue may briefly render as "In progress: #N" on the list page; the progress bar, `readyToMarkDone`, `nextIssue`, and `CanStart` remain exact.
