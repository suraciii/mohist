## Context

The issue detail page already receives the live event stream: `useEventsConnection` (SignalR) feeds `LiveTaskProvider`, which unwraps each event, routes it through `handle-event.ts` (per-domain query-key invalidations + cross-issue toasts), and forwards it to `dispatchTimelineEvent` (the bus the Activity view reads). So the stream, the routing, and the cache-invalidation side effects are all in place.

Despite that, the page's primary update driver is not the stream — it is `useWorkflowTimeline`'s `refetchInterval: enabled ? 5000 : false` (`entities/issue/api/queries.ts:158`). Every five seconds the timeline query refetches and the components that consume it re-render. Separately, the page's `decision.summary` (from `deriveRuntimeDecision`) already adjudicates the states that matter to an owner — `approval-required`, `blocked`, etc. — but the toast helpers (`notifyRunLifecycleToast`, `notifyApprovalRequestedToast` in `app/providers/model/run-lifecycle-toast.ts`) deliberately suppress toasts when `issueNumber === viewedIssue`, on the assumption that a viewer of that issue does not need notifying.

Two consequences follow that this change corrects. First, the 5 s timer is redundant in steady state: every workflow event that mutates the timeline already invalidates `['issues']`, and TanStack Query's prefix cascade re-touches `['issues', n, projectId, 'workflow-timeline']` (and the issue, diff, and commits keys) without a timer. Second, the toast-suppression rule means the one owner who most needs a nudge — the owner with the issue page open in a background tab — never gets one.

This is a Web-only data-flow and presentation change. The Server remains the source of truth; no event types, routing, DTOs, or persistence change. Primary stakeholders are issue owners keeping the page open while a run proceeds, including on phones.

## Goals / Non-Goals

**Goals:**

- Make the existing event stream the page's primary update source so workflow transitions appear the moment they happen.
- Remove the steady-state 5 s timeline poll; keep a catch-up refetch only when the events connection drops and reconnects.
- Preserve the reader's scroll position and every expanded/collapsed section across each live update.
- Raise an attention toast when the viewed issue enters an approval-waiting or blocked state while the page is open, exactly once per transition, without duplicating the global cross-issue toast.
- Deliver the above identically on a phone-width viewport.

**Non-Goals:**

- New event types, event-routing changes, or any server/runner/CLI change.
- Real-time updates for the board, dashboard, inbox, or any other page.
- Browser push / notification API or PWA background notifications.
- Restructuring page sections, tiers, or the decision surface (owned by sibling issues; this change rides on the current composition).
- Changing how diff, commits, or comments refresh (the `['issues']` cascade already covers them; their freshness cadence is unchanged).

## Decisions

### D1. Remove the steady-state poll from `useWorkflowTimeline`; the page owns the reconnect catch-up

Set the hook's `refetchInterval` to `false` so no recurring timer runs in any consumer. The catch-up refetch, when the events connection drops and reconnects, is owned by `IssueDetailPage`, not the hook. The page reads the events reconnect signal (see D5) and calls `timelineQuery.refetch()` from a `useEffect` keyed on that signal. Once caught up, no timer resumes.

Rationale: the data hook stays a pure read with no hidden timer and no dependency on app-layer connection state. The "when to catch up" policy lives in the page, which is the subject of the spec. All current `useWorkflowTimeline` consumers live on the issue detail page (`IssueDetailPage`, `useEventTimeline` in the Activity dialog, `StageBar`/`WorkflowView`); the Activity dialog already merges live events itself, so none of them regress when the timer is removed.

Alternative considered: read the reconnect signal inside `useWorkflowTimeline` so every consumer gets catch-up automatically. Rejected because it couples an entity-layer query hook to the app-layer events connection and hides update policy in a data hook.

Alternative considered: keep the 5 s poll but only while the connection is `disconnected`/`reconnecting`. Rejected because it reintroduces a timer in the error/reconnecting window and races the reconnect-driven catch-up; an explicit one-shot catch-up on reconnect is deterministic.

### D2. Ride the existing `['issues']` cascade for stage/workflow/approval events, and add ROUTE entries for task (and artifact) events the client already receives but currently drops

Stage, workflow-run, issue, and approval handlers in `handle-event.ts` already invalidate `['issues']`. TanStack Query invalidates by prefix, so that single call re-touches every key the page reads: `['issues', n, projectId]` (issue), `['issues', n, projectId, 'workflow-timeline']`, `['issues', n, projectId, 'diff']`, and `['issues', n, projectId, 'commits']`. Once D1 removes the competing 5 s timer, those coarse-grained transitions reach the page incrementally with no further work.

Task transitions are the exception and must be wired explicitly. `TaskStarted`/`TaskCompleted`/`TaskFailed` (and `ArtifactRecorded`) are canonical, subscribed events (`canonical-event-types.ts:15-18,87-91`), so the hub delivers them and `LiveTaskProvider.handleEvent` receives them — but they are absent from the `ROUTE` table (`handle-event.ts:237-274`) and from `AGENT_ACTIVITY_EVENT_NAMES` (`handle-event.ts:55-71`), so today they only forward to the Activity timeline bus (`dispatchTimelineEvent`) and never invalidate the page's queries. Because the page's task-progress UI reads solely from the `useWorkflowTimeline` cache (`StageBar.tsx:48-59`, `WorkflowView.tsx:2,9-16`), removing the 5 s poll without this wiring would leave task completions stale until some other routed event happens to fire — directly violating AC line 1 and spec Requirement 1. Add a `taskHandler` (and `artifactHandler`, or one shared handler) to `ROUTE` that invalidates `['issues']`, matching the `stageHandler` pattern. This is purely client-side invalidation wiring for events the client already subscribes to and receives; it is not a server-side event-routing change and not a new event type, so it stays within the issue's "no new event types / no server-side event changes" boundary. The issue's "event routing" non-goal is read as the server-side subscription/routing topology, not the web client's invalidation-side-effect table, which stage/workflow/approval events already populate.

Rationale: the cascade covers the coarse-grained transitions; task/artifact events are the only subscribed events the page's queries silently drop. Reusing the existing ROUTE + invalidation mechanism keeps a single invalidation model instead of introducing a parallel local-merge path. The only key not covered is `['issue-events', n, projectId]` (Activity history), which is intentionally out of scope.

Alternative considered: have the page merge task events locally off the `onTimelineEvent` bus into the task UI, the way `useEventTimeline` does. Rejected because it splits the page's task data between the query cache and a local merge, diverges from the single-source-of-truth timeline query, and duplicates status-merging logic. (`useEventTimeline`'s bus-merge is reused only by the Activity dialog, which serves evidence, not the page's task UI.)

Alternative considered: narrow the spec to drop task start/completion from the live guarantee. Rejected because it contradicts the issue's headline acceptance criterion ("a task completion … appear without reload").

### D3. Page-owned, edge-triggered attention nudge on `decision.summary` transition

Add a small page-local hook (e.g. `useIssueAttentionNudges`) that watches the already-computed `decision.summary` and raises a toast when it transitions INTO `approval-required` or `blocked`. The hook keeps a ref of the previous summary, initialises it to the current summary on mount (so navigating to an already-approval-waiting issue does NOT toast on arrival), and fires only on a subsequent transition into the target state. It reuses `deriveRuntimeDecision` as the single adjudication source and the existing `toast` (sonner) surface.

This deliberately does not wire toasts to raw event types. "Approval-waiting" and "blocked" are derived summaries whose causes vary (approval gate, drift needs-attention, convergence blocking, rebase conflict, run failure); adjudicating off the derived summary means the nudge matches exactly what the page presents, fires only on meaningful transitions (satisfying the spec's "limited to meaningful transitions" requirement), and fires once per transition (satisfying "exactly once").

The global cross-issue toasts in `handle-event.ts` are left unchanged: they remain suppressed for `issueNumber === viewedIssue`. The page-level nudge is therefore the sole toast for the viewed issue, and cross-issue toasts for other issues keep their current behavior. No double-notice.

Rationale: reusing the canonical adjudication avoids duplicating "what counts as approval-waiting/blocked" across event types and keeps the nudge aligned with the page's own presentation. Edge-triggering off the summary is the natural unit of "meaningful transition."

Alternative considered: fire the nudge from `handle-event.ts` by removing the `viewedIssue` suppression for approval/blocked events. Rejected because it duplicates the cause-to-summary mapping at the event layer, risks per-event (not per-transition) firing, and entangles global and per-page concerns in one handler.

Alternative considered: fire on mount when the page loads into an approval-waiting state. Rejected; navigating to an issue that needs approval already shows the approval surface, so a toast would be redundant and noisy. The nudge is for transitions that occur while the page is open.

Nudge set scope: the hook toasts only on transitions into `approval-required` and `blocked`, matching the issue's acceptance criteria. A run that enters the separate `failed` summary while the page is open is deliberately not toasted for the viewed issue: the failure is presented inline by the decision surface, and the existing global cross-issue toast (`notifyRunLifecycleToast` kind `'error'`, `handle-event.ts:164-167`) still covers failed runs on other issues. This is a recorded scoping decision; expanding the set is out of scope for this change and can be revisited if owners report missing failed-run transitions.

### D4. Reading stability is an audit, not a rewrite — stable keys + first-load-only loading guard

The page already survives background refetches: `IssueDetailPage.tsx:215` gates on `isLoading || !issue`, and TanStack Query's `isLoading` is first-load only (it is `false` during a background refetch that has prior data), so the tree is not unmounted on each update. Section collapse/expand state lives in `CollapsibleRailCard`'s `useState` and survives as long as the component instance is not remounted. The page-critical rendered lists already use identity-stable keys (`StageBar` `key={stage}`, `WorkflowSessionRow` `key={session.id}`, `InlineApproval` `key={task.taskId}`, `TaskItem` `key={summary.artifactId}`, `FeedbackHistory` `key={item.id}`).

The change is therefore to (a) preserve these invariants and (b) lock them with tests: assert no early-return loading branch fires during a background refetch (guard against any newly introduced `isFetching`-based early return), and assert no page-critical list introduces index/position-derived keys. A few index keys exist in non-page-critical widgets (`ReviewSummary`, `IssueWorkflowProfileEditor`, `TaskLogPanel` milestones); they are out of scope unless an audit shows they affect reading stability.

Rationale: the stability properties mostly hold today; the work is to keep them holding once the update driver switches from a timer to the stream, and to prevent regressions via tests.

### D5. Expose the events reconnect signal to the page

`useEventsConnection` already returns `reconnectVersion` (incremented on each `onreconnected`) and `status`, but `LiveTaskState` only surfaces `{ activeTaskId, activeTaskElapsedMs, rebaseConflict }`. Extend `LiveTaskState` with the reconnect signal (recommended: `eventsReconnectVersion: number`) captured from the `eventsConnectionHook` return, consumed by the page via the existing live-task context. The page uses it as the sole trigger for the D1 catch-up `refetch()`.

Alternative considered: a dedicated `EventsConnectionContext`. Rejected as a separate context just for one number; `LiveTaskState` already exists and already carries realtime-layer derived state. If `LiveTaskState` grows further, a split can be revisited.

### D6. Mobile parity is structural, not a separate workstream

The events connection, query invalidation, reconnect signal, derived decision summary, and toast surface are all viewport-independent. The narrow-viewport renderers (`MobileActionBar`, the mobile padding/offset paths) consume the same query data and the same `decision` object as desktop. Live updates, scroll/section stability, reconnect catch-up, and attention nudges therefore hold on a phone-width viewport by construction. The spec coverage asserts each behavior at a phone-width viewport to lock the parity rather than rely on it being obvious.

### D7. Blocked-state coverage is the union of event-driven invalidations and the retained workspace-status poll

The nudge (D3) is edge-triggered off `decision.summary`, which recomputes whenever the issue or timeline query data changes. The causes of a transition into `blocked` map to different update feeds, and each must be accounted for so coverage is honest rather than assumed:

- **Run failure** (`WorkflowRunFailed`) — routed via `workflowRunHandler` → invalidates `['issues']`. Event-driven; covered by D2.
- **Approval gate** (`StageApprovalRequested`) — routed via `approvalHandler` → invalidates `['issues']`. Event-driven; covered by D2.
- **Stage failure / stage transitions that flip the summary** — routed via `stageHandler` → invalidates `['issues']`. Event-driven; covered by D2.
- **Drift needs-attention and convergence blocking** — there is no canonical reverse-DNS event for these; `issue.drift` and `issue.convergence` are part of the issue projection and are also surfaced through `useWorkspaceStatus`, which retains its own `refetchInterval` (5 s / 30 s) in `entities/issue/api/queries.ts:170-180`. These update when the issue query refetches on any `['issues']` invalidation, or when the workspace-status poll fires.

Implication: a drift/convergence-driven block is not guaranteed to be event-driven; its timeliness is bounded by the retained `useWorkspaceStatus` poll plus whatever `['issues']` invalidation happens to fire. D1 removes only the *timeline* poll; the workspace-status poll is intentionally retained so drift-driven blocks continue to surface without introducing a new event type (a non-goal). The spec's blocked requirement is therefore satisfied on the same terms it is today — no regression — and the nudge fires once `decision.summary` actually transitions. The event-driven causes (failure / approval / stage) become timely through D2.

This is an explicit acknowledgement, not a new mechanism: blocked coverage is no worse than today, and the event-driven causes become timely.

## Risks / Trade-offs

- `[Risk] Removing the steady-state poll lets a missed event leave the page stale` -> The events stream is the primary path; the D1 reconnect catch-up refetch covers the drop/reconnect window. A poll-free design is the spec's explicit target.
- `[Risk] Adding task/artifact ROUTE entries over-refetches the page on frequent task events` -> Task transitions are low-frequency (a few per stage) and fire only on actual change; the removed 5 s poll refetched unconditionally every 5 s, so event-driven invalidation is strictly less traffic. The broad `['issues']` cascade is reused intentionally for consistency with `stageHandler`.
- `[Risk] A flaky events connection triggers repeated catch-up refetches, thrashing the timeline` -> Reconnect catch-up is edge-triggered off `reconnectVersion`, which increments only on a successful `onreconnected`, not on each transient `reconnecting` tick; exponential backoff in `createEventsConnection` bounds reconnect frequency.
- `[Risk] The attention nudge double-fires with the global cross-issue toast` -> The global path already suppresses for `viewedIssue`; the page nudge is the only viewed-issue toast. Asserted in spec coverage.
- `[Risk] The nudge fires on mount for an issue already awaiting approval` -> The hook initialises its previous-summary ref to the current summary on mount and fires only on a subsequent transition; asserted in tests.
- `[Risk] A live update remounts the tree and resets scroll/section state` -> The `isLoading` guard is first-load only and must stay so; stable identity keys must be preserved. Locked by spec coverage that applies a live update mid-read and asserts scroll position and a toggled section survive.
- `[Risk] The page-level nudge and the Activity dialog's live merge diverge in what events they honor` -> Both consume the same stream; the nudge adjudicates off `decision.summary`, Activity off `dispatchTimelineEvent`. They serve different purposes (attention vs. evidence feed) and need not be unified.
- `[Trade-off] The catch-up refetch policy lives in the page, not the hook` -> Keeps the data hook pure and the policy local to the spec's subject, at the cost of each future live consumer reimplementing a one-line reconnect effect if more are added.
- `[Trade-off] Nudges are DOM-rendered toasts, not OS notifications` -> A backgrounded tab surfaces the toast on return (it persists until dismissed/auto-close). This matches the non-goal of no push/notification API; owners who close the tab are not reached (by design).

## Migration Plan

1. Extend `LiveTaskState` (and `useLiveEvents`) to carry the events reconnect signal from the existing `useEventsConnection` return; expose it through the live-task context.
2. Set `useWorkflowTimeline` `refetchInterval` to `false`; add the page-owned reconnect catch-up `useEffect` in `IssueDetailPage` keyed on the new signal.
3. Add the task/artifact ROUTE entries to `handle-event.ts` (a `taskHandler`/`artifactHandler`, or one shared handler, that invalidates `['issues']` per the `stageHandler` pattern) so `TaskStarted`/`TaskCompleted`/`TaskFailed`/`ArtifactRecorded` events the client already receives drive live updates of the page's task progress and artifacts. Verify a `TaskCompleted` event (distinct from any stage/workflow-run event) updates the page with no poll and no reload.
4. Add `useIssueAttentionNudges` (edge-triggered off `decision.summary` into `approval-required` / `blocked`, previous-summary ref initialised on mount) and wire it into `IssueDetailPage`.
5. Audit and lock reading stability: confirm the loading guard remains first-load only (no `isFetching` early return) and page-critical lists keep identity-stable keys.
6. Add spec coverage: incremental update without re-render (task completion driven by the new ROUTE entry + stage transition via the existing cascade), scroll-position and section-state survival across a live update, no steady-state poll + reconnect catch-up, attention toast on approval-waiting and on blocked (and not on routine transitions, not on mount, not duplicated with the global toast, not fired on a `failed`-only transition), each repeated at a phone-width viewport. Blocked-cause coverage asserts the event-driven causes (run failure, approval, stage failure) trip the nudge via D2, and acknowledges drift/convergence ride the retained workspace-status poll per D7.
7. Update existing `pages/issue-detail/ui/IssueDetailPage.*` tests where the update-flow change affects them; preserve existing `data-testid` anchors.

No data migration, feature flag, or staged rollout is required. The change ships in one Web bundle against unchanged APIs. Rollback is a Web-code revert; no data repair or compatibility step is needed.

## Open Questions

None. The proposal and capability specs fix the update source (the existing stream plus the existing task/artifact events newly wired into client-side invalidation), the polling posture (reconnect fallback only), the nudge trigger set (approval-waiting and blocked, edge-triggered off the canonical summary), and the unchanged API/event boundary. Two scoping decisions are recorded in the body rather than left open: the nudge set is intentionally approval + blocked per the AC with `failed` excluded (D3), and the "event routing" non-goal is read as server-side routing/new event types, with client-side invalidation wiring for already-received task/artifact events in scope (D2).
