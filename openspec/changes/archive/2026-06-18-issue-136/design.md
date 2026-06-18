## Context

The server already ships a merged, chronological event feed (`GET /api/projects/{projectRef}/issues/{number}/events`, `WorkflowEventRoutes.cs`) that returns both `IssueEvents` and `WorkflowRunEvents` sorted by time as `StoredCloudEventDto[]`. Live events already reach the browser over SignalR (`/hubs/events` → `LiveTaskProvider.handleEvent`), but `handleEvent` only calls `queryClient.invalidateQueries` and fires toasts — it never accumulates or displays the events themselves. The WebUI has no client call to the `/events` endpoint and no event-timeline widget.

The existing frontend provides all the building blocks this feature needs:
- `shared/api/client.ts` — `request<T>()` + `projectApiPath()` helpers for API calls.
- `entities/issue/api/{client,queries}.ts` — the pattern for issue-scoped API functions + React Query hooks.
- `entities/issue/model/rebase-events.ts` — a lightweight `EventTarget`-based pub/sub (`dispatchRebaseEvent` / `onRebaseEvent`) for accumulating live events outside React render.
- `app/providers/LiveTaskProvider.tsx` — receives every SignalR event and already extracts `issueNumber` from payloads.
- `shared/lib/canonical-event-types.ts` — the full reverse-DNS + legacy event type vocabulary.
- `widgets/issue-workflow/` — the widget module pattern (`ui/` + `index.ts` barrel).
- `pages/issue-detail/ui/IssueDetailPage.tsx` — the main column (`lg:col-span-2`) where Description, diff summary, commits, and Comments already live.

This change is **frontend-only**. No backend, event persistence, or event-type changes.

## Goals / Non-Goals

**Goals:**
- A new Activity panel on Issue Detail showing a merged, time-ordered feed of issue + workflow events.
- Load history from the existing `/events` endpoint on page open.
- Append live events in real time via the existing SignalR bus with enter animation.
- Human-readable descriptions, category color coding, attention emphasis, category filters, and a Live badge.
- Minimal, surgical integration with the existing live event path — no disruption to existing cache-invalidation or toast behavior.

**Non-Goals:**
- Backend changes, new event types, or changes to the event persistence model.
- Replacing the stage/task view (`WorkflowView`) or the runtime decision surface.
- Surfacing coder-session transcript turns (that is the session transcript).
- A cross-issue global event feed.
- Editing issue/workflow state from the timeline.

## Decisions

### D1: New widget module `widgets/issue-event-timeline/`

The timeline is a distinct, self-contained surface (like the Logs page or Session Timeline). It gets its own widget module following the existing `widgets/issue-workflow/` pattern:

```
widgets/issue-event-timeline/
  index.ts                          # barrel: exports EventTimelinePanel
  model/
    types.ts                        # TimelineEntry, TimelineCategory, etc.
    classify.ts                     # classifyEvent(type, payload) -> { category, attention }
    describe.ts                     # describeEvent(type, payload) -> human-readable string
    source-tag.ts                   # classifySource(type) -> 'ISSUE' | 'WORKFLOW'
  ui/
    EventTimelinePanel.tsx          # main panel: header (Live badge, filters, order toggle) + list
    EventTimelineRow.tsx            # single row: time · dot · [tag][source] message (+detail)
    CategoryFilter.tsx              # category chips with counts
```

**Alternative considered:** Placing it inside `widgets/issue-workflow/`. Rejected because the timeline is observation-only and conceptually independent from the workflow progress widgets; co-locating would bloat that module.

### D2: API client + query hook with a non-`['issues']` query key

Add `getIssueEvents(number, projectId)` to `entities/issue/api/client.ts` and `useIssueEvents(number)` to `queries.ts`, following the existing `getIssueDiff` / `useIssueDiff` pattern.

**Critical detail:** The query key SHALL be `['issue-events', number, projectId]` — NOT `['issues', number, projectId, 'events']`. The `LiveTaskProvider` calls `queryClient.invalidateQueries({ queryKey: ['issues'] })` on every live event, which is a prefix match. A key starting with `['issues', ...]` would trigger a refetch on every single live event, defeating the client-side accumulation + animation design and creating unnecessary server load. The `['issue-events', ...]` key is only fetched on mount and issue change.

The function calls `request<StoredCloudEventDto[]>(projectApiPath(projectId, '/issues/${number}/events'))`.

**Alternative considered:** Invalidating the events query on live events and relying on refetch. Rejected because it loses the enter-animation UX, adds server load proportional to event frequency, and introduces refetch-latency between event arrival and display.

### D3: Live event accumulation via EventTarget pub/sub (mirrors `rebase-events.ts`)

Add a new `entities/issue/model/timeline-events.ts` that follows the exact `rebase-events.ts` pattern:

```typescript
export type TimelineLiveEvent = {
  issueNumber: number | null
  issueId: string | null
  type: string
  time: string | null
  payload: Record<string, unknown>
}
const target = new EventTarget()
export function dispatchTimelineEvent(event: TimelineLiveEvent): void { ... }
export function onTimelineEvent(handler): () => void { ... }
```

In `LiveTaskProvider.handleEvent`, after the existing switch (which preserves all invalidation/toast behavior), add a single forward call:

```typescript
const issueNumber = readIssueNumber(parsed)
const issueId = (parsed.issueId as string | undefined) ?? null
dispatchTimelineEvent({ issueNumber, issueId, type: eventName, time: readTime(parsed), payload: parsed })
```

This is additive — the existing `invalidateQueries` and toast logic runs unchanged before the dispatch.

**Alternative considered:** A new React Context that `LiveTaskProvider` populates. Rejected because the EventTarget pattern is already established for rebase events, is framework-light, and avoids re-rendering the provider tree on every event.

### D4: Merge + dedup in a `useEventTimeline` hook inside the widget

The widget's `EventTimelinePanel` uses a `useEventTimeline(issueNumber, issueId)` hook that:

1. Reads history from `useIssueEvents(number)` (React Query, fetched once per issue).
2. Maintains a `useRef<TimelineEntry[]>` of live events accumulated via `onTimelineEvent`.
3. Resets live events when `issueNumber` changes.
4. Merges history + live events, deduplicating by CloudEvents `id` (`StoredCloudEventDto.eventId` for history; envelope `id` / payload `eventId` for live). Falls back to a composite key of `type + time` if `id` is absent.
5. Returns the merged, sorted list.

The hook normalizes both sources into a common `TimelineEntry`:
```typescript
type TimelineEntry = {
  id: string           // dedup key
  type: string         // event type
  time: string         // ISO timestamp
  source: 'ISSUE' | 'WORKFLOW'
  category: TimelineCategory
  attention: boolean
  description: string
  detail: string | null  // mono detail for failures (file paths, errors)
  payload: Record<string, unknown>
}
```

### D5: Category classification as a priority-ordered pure function

`classify.ts` maps event types to one of six categories. Classification is priority-ordered so that overlapping types resolve correctly (e.g., `merge_completed` is Success, not Integration; `rebase_conflict` is Failure, not Integration):

1. **Failures** (red): type contains `failed`/`fail`/`conflict`/`error`, or `base_drift_detected` with `decision === 'needs-attention'`.
2. **Success** (green): type contains `completed` (stage/run/merge completed).
3. **Approval** (amber): type contains `approval-requested`, `approval-resolved`, `paused`.
4. **Integration** (purple): type matches rebase/merge/check/integration prefixes.
5. **Metadata** (gray): type matches labels/priority/prerequisite/comment.
6. **Workflow/lifecycle** (blue): everything else (run started/stopped/resumed, stage started, task updates).

`attention` is `true` for Failures and Approval-requested events.

### D6: Source tag from event type prefix

`source-tag.ts` classifies by type prefix:
- `com.mohist.issue.*` and legacy `comment_added` → `ISSUE`
- `com.mohist.workflow.*` and legacy stage/run/rebase/merge/check/integration types → `WORKFLOW`

### D7: Human-readable description formatter

`describe.ts` maps each known event type to a template string using payload fields. Examples:
- `com.mohist.workflow.stage.started` `{from, to}` → "Stage moved from {From} to {To}"
- `com.mohist.workflow.stage.approval-requested` `{stage}` → "Approval requested for {Stage}"
- `com.mohist.issue.labels-changed` `{labels}` → "Issue labeled {labels}"
- `rebase_conflict` `{conflicts}` → "Rebase conflict detected on {n} files"

Stage names are title-cased. Unknown types fall back to a prettified type string (hyphens/dots → spaces, title-cased) so no event ever shows a raw type token unreadably.

### D8: Placement in IssueDetailPage main column

Insert `<EventTimelinePanel issueNumber={issueNumber} issueId={issue?.id} workflowStatus={issue?.workflowStatus} />` in the `lg:col-span-2` main column, between the commits/diff unavailable block (line ~649) and the Comments section (line ~651).

### D9: Live badge derived from issue workflow status

The Live badge pulses when `issue.workflowStatus === 'running'`. This field is already on the Issue model and already refreshed via the existing `['issues']` invalidation. No new data source is needed.

## Risks / Trade-offs

- **[Live event dedup failure if CloudEvents `id` is absent]** → Mitigation: composite fallback key `type + time`. The server's `StoredCloudEventDto` always includes `eventId`, and the SignalR envelope always includes `id`, so this is a safety net only.
- **[Accumulated live events grow unbounded during a long run]** → Mitigation: cap the live accumulator at 500 entries (drop oldest). History is already capped by the server default `limit=200`.
- **[Query key prefix collision with `['issues']` invalidation]** → Mitigation: use `['issue-events', ...]` key (D2). This is the most important correctness detail.
- **[Event arrives over SignalR before history loads]** → Mitigation: the merge always includes both sets; a live event that's also in history is deduped, so ordering is preserved regardless of arrival timing.
- **[Category misclassification for future event types]** → Mitigation: the priority-ordered classifier falls through to Workflow/blue for unknown types, and the description formatter falls back to a prettified type string. New event types degrade gracefully rather than breaking.

## Migration Plan

This is a purely additive frontend change with no backend, database, or API contract changes. There is no data migration.

**Rollout:** The new widget and API client ship in the Web bundle. The only integration points are (1) the new `<EventTimelinePanel>` render in `IssueDetailPage` and (2) the `dispatchTimelineEvent` call in `LiveTaskProvider.handleEvent`. Both are additive.

**Rollback:** Revert the two integration points. The widget module, API client, and pub/sub are dead code if not rendered/called. No server-side rollback needed.

## Open Questions

- **Polling fallback for history:** Should the events query refetch periodically (like `useWorkflowTimeline`'s 5s poll) as a safety net for missed live events, or is mount-only fetch + live accumulation sufficient? **Recommendation:** mount-only + live accumulation for v1; add polling only if missed events are observed in practice.
- **Virtualization for very long timelines:** At 200 history + 500 live cap, the DOM stays manageable. If users report lag on long-running issues, add virtualized rendering as a follow-up. Not needed for v1.
