# Proposal: Start issues directly from the epic page (inline start)

## Why

Starting the next startable issue today forces a hop into the issue detail page, even though the epic page already computes and displays that issue and its startability (`canStart` / `blocker`). For low-risk, fire-and-forget work this extra navigation breaks the epic-as-control-tower flow. The gating signals are already present on the epic read model; the missing piece is a start action on the epic surfaces themselves.

## What Changes

- Epic list card "next issue" area: when the next issue is startable (`canStart`), expose a **Start** action inline on the card; when it is not startable, continue to show the blocker reason (e.g. "Waiting on #N", "Still a draft").
- Epic detail page `LinkedIssueRow`: add a **Start** action for linked issues that `canStart` and are not in a terminal or in-flight state; hide Start for in-progress / done / cancelled / blocked issues (their existing navigation and Remove action are unchanged).
- Reuse the existing issue start path (`POST /issues/{n}/start` via `IssueGrain.StartWorkAsync`). No new start endpoint, no change to start semantics, no batch start.
- On success, invalidate epic and issue query caches so the issue enters `in_progress` and epic progress / next-issue / current-activity refresh together; on failure, surface a toast.
- Extend the web `LinkedIssue` type with `canStart` and `blocker` so the epic detail surface can gate the new action off the same DTO already emitted by the server.

Non-goals (explicit): no batch start; no start node on the dependency graph; no change to issue start / approval / workflow semantics; no change to `canStart` / `Blocker` computation (only consumed).

## Capabilities

### New Capabilities

- `epic-inline-start`: Starting an issue directly from epic surfaces (the epic list card next-issue area and the epic detail linked-issue row), gated on the issue's derived `canStart`, reusing the existing issue start path, with epic + issue cache invalidation on success and toast feedback on failure.

### Modified Capabilities

None. `epic-board` already requires the card to surface the in-progress and next issue and the detail page to list linked issues; this change adds a write action on top of those display surfaces without altering their existing requirements. `issue-start-readiness` is consumed unchanged (its `canStart` / `Blocker` derivation is the gating signal, not modified).

## Impact

- **Web (primary surface):**
  - `packages/web/src/pages/epics/ui/EpicListPage.tsx` — `EpicCard` next-issue area gains a Start action gated on the startable next issue.
  - `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx` — `LinkedIssueRow` gains a Start action for `canStart` non-terminal issues.
  - `packages/web/src/entities/epic/model/types.ts` — `LinkedIssue` extended with `canStart` / `blocker` (and the detail query mapping updated to the DTO already sent by the server).
  - Reuse of `startIssue` from `packages/web/src/entities/issue/api/client.ts`; TanStack Query invalidation across epic (`epics`, epic detail) and issue query keys; toast on failure.
- **Server:** no API change. `LinkedIssueDto.CanStart` / `StartBlocker` (delivered by #171, populated in `EpicQuerier.GetLinkedIssuesAsync`) and `EpicProgressDto.NextIssue` / `NextIssueReason` already carry the gating signals. Start continues to flow through the existing `IssueGrain.StartWorkAsync` / `POST /issues/{n}/start`.
- **Risk:** medium — a write action (start, which triggers a workflow run) placed on a read surface, crossing epic and issue query caches and touching in-progress / failure feedback.
