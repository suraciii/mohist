## Why

When a user opens an issue to watch a run or diagnose a failure, there is no place that shows what is happening — or what has happened — in order. Stage transitions, the approval lifecycle, rebase/merge/check/integration steps, label and priority changes, comments, base drift, and errors all occur, but on the WebUI they only surface as silent background cache refreshes or as transient toasts that disappear. The user cannot answer "is it actually progressing or stalled?", "what was the exact sequence when it failed?", or "when did it move from check to integrate?" without stitching together multiple surfaces, and after the fact the sequence is gone from the page. The server already ships a merged chronological event feed and live events already reach the browser over SignalR, but the WebUI never accumulates or displays them.

## What Changes

- Add a new **Activity** panel to the Issue Detail main column, placed between the diff/commits area and Comments. It is a read-only observation surface that complements the stage/task view (above) and the runtime decision surface.
- The panel renders a single time-ordered merge of issue events and workflow events, loaded from the existing `GET /api/projects/{projectRef}/issues/{number}/events` endpoint, with each row tagged `ISSUE` or `WORKFLOW` to disambiguate the two streams.
- Each entry is human-readable (a clear verb/subject such as "Stage moved from Plan to Code", "Rebase conflict detected on 3 files", "Approval requested for Check", "Issue labeled bug") with a timestamp, rather than a raw event type string.
- While the issue is open and its workflow is active, new events are appended to the timeline in real time over the existing SignalR bus, with a pulsing **Live** badge and a top-enter animation — no full page reload.
- Apply a category color system (workflow/lifecycle = blue, approval = amber, integration = purple, success = green, failures = red, metadata = gray) that reuses the existing pill/dot palette.
- Visually emphasize failures and attention-required events (stage/run/merge failed, rebase conflict, approval requested, base drift needs-attention) with a tinted background and haloed dot, and expand an inline mono detail block for failures.
- Provide category filter chips with counts (mirroring the Logs page level-filter pattern) so a noisy active run can be narrowed to e.g. just Failures + Approval.
- Default to newest-first ordering so live events appear without scrolling, with a toggle to flip to chronological order; show sticky day separators when the feed spans days.
- Accumulate live workflow/issue events client-side as they arrive, rather than only invalidating query caches as `LiveTaskProvider.handleEvent` does today.

## Capabilities

### New Capabilities

- `issue-event-timeline`: A read-only, real-time event timeline surface on the Issue Detail page that merges issue lifecycle events and workflow run events into one chronological, human-readable feed with category color coding, attention emphasis, category filters, and live updates over the existing SignalR bus.

### Modified Capabilities

- `web-ui`: The Issue Detail page gains a new Activity panel (between the diff/commits area and Comments) and a client-side call to the existing `/issues/{number}/events` endpoint, and the live event path accumulates events for display in addition to its existing cache-invalidation/toast responsibilities.

## Impact

- `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx` - render the new Activity panel between the diff/commits section and Comments.
- New Web feature/widget module (e.g. `packages/web/src/widgets/issue-event-timeline/` or `features/issue-event-timeline/`) - the timeline component, the row renderer, the category color/attention classifier, and the human-readable description formatter driven by the existing `EventMap` vocabulary.
- `packages/web/src/entities/issue` - add a query/typed client for `GET /api/projects/{projectRef}/issues/{number}/events` returning the existing `StoredCloudEventDto` shape, plus a hook that seeds from that query and accumulates matching live events.
- `packages/web/src/app/providers/LiveTaskProvider.tsx` and `packages/web/src/shared/api/events-hub.ts` - forward live issue/workflow events to the new timeline accumulator (the existing cache-invalidation and toast behavior is preserved).
- `packages/web/src/shared/lib/canonical-event-types.ts` and `EventMap` - reused as the event vocabulary source; no new event types are introduced.
- No backend changes: `WorkflowEventRoutes.cs`, `IEventStore.ListIssueEventsAsync`, `ListAsync`, the `IssueEvents`/`WorkflowRunEvents` tables, and the SignalR `/hubs/events` delivery path are already shipped and unchanged.
- Web tests covering history load, live append, ordering, category classification, attention emphasis, and merged-stream correctness.
