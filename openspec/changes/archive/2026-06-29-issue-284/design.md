## Context

The durable project inbox (issue #286, shipped) is the product fact: four
authoritative CloudEvents (`workflow.run.failed`, `stage.approval-requested`,
`issue.work-started`, `issue.work-completed`) are projected by
`InboxProjectionHandler` into `InboxItem` rows via `InboxStore.InsertAsync`,
which deduplicates on `(SourceEventSource, SourceEventId)`. The inbox HTTP API
(`GET /api/projects/{projectRef}/inbox`, mark-read, read-all, archive) stays
authoritative; the Web derives the unread count client-side from that list.

What is missing is the **delivery hint**: today the inbox only updates on a
manual refresh, and the existing `useInboxLiveRefresh` hook is a poor
substitute — it opens a **second** SignalR connection and refetches the list on
every *source* event, regardless of whether a new inbox item was actually
persisted (duplicates and rejected projections still trigger refetches), and it
has no server-side project filter on the wire.

The realtime transport we need already exists:

- `MohistHub` (`/hubs/events`) — per-connection subscription model. The browser
  calls `SetSubscriptionsAsync(eventTypes)`; the server pushes via
  `IEventsClient.OnEvent(eventName, envelope)`.
- `EventBridge` (`[Subscription("com.mohist.*")]`) — fans each CloudEvent to
  every connection the dispatcher approves.
- `UserNotificationDispatcher.ResolveTargetConnectionsAsync` — today filters
  **by event type only**. Its own doc (`IUserNotificationDispatcher.cs:30-46`)
  already anticipates filtering by the event's `projectid` / `issueno`.
- The established project-routing convention is `extensions["projectid"]`,
  stamped by `IssueGrain.cs:605` and read by the inbox projection.

Two gaps must be closed for this feature:

1. **No server-side project affinity.** `MohistHub.OnConnectedAsync` ignores the
   `?projectId=` query string the Web already sends. Type-only filtering means a
   project-A tab subscribed to an inbox hint type would receive project-B hints.
   The spec forbids this leakage, so server-side isolation is mandatory.
2. **No hint is emitted.** The projection never tells the transport layer that a
   new inbox item exists.

Constraints from the proposal/spec: no API contract changes, no schema changes,
no runner/workflow/issue-lifecycle changes, no browser/mobile/email/desktop push,
and the durable `InboxItem` remains the only truth — the realtime hint is
invalidation only.

## Goals / Non-Goals

**Goals:**

- Emit exactly one project-scoped "inbox item persisted" hint **strictly after**
  a non-duplicate `InboxStore` insert, carrying identity only (project + item).
- Deliver the hint with **strict server-side project isolation** over the
  existing `MohistHub` / `EventBridge` / per-connection subscription path.
- Treat the hint as **invalidation only**: the Web re-queries the inbox HTTP API;
  dropped/reconnected hints recover via the next query.
- Surface a **live unread count** in the app shell / project navigation.
- **Live-insert/refresh** the inbox page on a hint for the current project,
  without a full page reload.
- Show a **lightweight in-app notice** for high-attention kinds
  (`workflow_failed`, `approval_requested`), with **duplicate-notice
  suppression** when the user is already viewing the relevant issue or the inbox
  page.

**Non-Goals:**

- No new HTTP endpoints, no schema changes, no `InboxItem` model changes.
- No runner, workflow-engine, issue-lifecycle, or existing timeline-event
  changes.
- No browser/mobile/email/desktop push, sound, or OS notification permission.
- No new multi-user routing.
- No replacement of the existing `useInboxLiveRefresh` source-event semantics
  for unrelated consumers — it is superseded for the inbox, not generalized.

## Decisions

### D1. Emit a new CloudEvent from the projection, strictly after a non-duplicate insert

Add `com.mohist.inbox.item-persisted` to `EventCatalog.ReverseDns`. In
`InboxProjectionHandler.ProjectAsync`, capture the `InboxInsertResult` returned
by `InsertAsync` and, when `!result.AlreadyExisted`, publish the hint via
`IEventPublisher.PublishAsync` with `extensions["projectid"] = draft.ProjectId`
and a minimal identity payload:

```
{ itemId, projectId, kind, issueId, issueNumber }
```

The publish is `await`-ed inline **after** the successful insert return, which
guarantees the spec's ordering invariant (persisted before hinted) for free on
the in-process bus. Because the projection already swallows and logs exceptions
in `HandleAsync`, a hint-publish failure cannot break the source-event
projection.

The projection already resolves `kind`, `projectId`, `issueId`, `issueNumber`
and the new `itemId` (`result.Id`) — no new lookups are needed.

**Alternatives considered:**

- *Direct-to-hub publisher (transcript-style, like
  `SignalRTranscriptEventPublisher`).* Keeps the audited CloudEvent bus free of
  "presentation" events and avoids bus re-entrancy. Rejected as the primary path
  because the proposal explicitly names `EventBridge` as the reuse path, and an
  inbox-item-persisted fact is a meaningful observation (unlike ephemeral
  transcript deltas), so riding the bus is defensible. This remains the fallback
  if bus noise becomes a concern.
- *Client-side inference from the existing source events (status quo).*
  Rejected — it cannot satisfy "strictly after a non-duplicate insert" and has
  no server-side project filter.

### D2. Deliver via the existing bus → EventBridge → per-connection path

Because `EventBridge` subscribes to `com.mohist.*`, publishing
`com.mohist.inbox.item-persisted` is automatically fanned out to subscribed
connections through `OnEvent`. No new SignalR method, no groups, no new routing
surface. The Web opts in by adding the type to `EVENT_TYPES`
(`canonical-event-types.ts`), exactly like every other event it receives.

### D3. Server-side project isolation via per-connection project affinity + a gated dispatcher filter

This is the only genuinely new server mechanism. Two small, additive changes:

1. **Capture project affinity per connection.**
   `ConnectionSubscriptionRegistry` gains a per-connection `projectId` (default
   `null`). `MohistHub.OnConnectedAsync` reads
   `Context.GetHttpContext()?.Request.Query["projectId"]` (already sent by
   `events-hub.ts`) and stores it. This mirror is the hot path the dispatcher
   reads; no grain change is required for correctness (project affinity is
   transport-level presentation state, intentionally not durable — see the
   `project-inbox` spec which keeps subscriptions as transport state).

2. **Generalized, backward-compatible project filter in
   `UserNotificationDispatcher.ResolveTargetConnectionsAsync`.** When the event
   carries an `extensions["projectid"]` **and** the connection has declared a
   project, the connection is included only on project match. When either side
   is absent, behavior is unchanged (type-only match). This satisfies the
   "strict isolation" requirement for the inbox hint and any other
   `projectid`-carrying event, without special-casing.

**Why generalized and not inbox-only.** The dispatcher's documented design
(`IUserNotificationDispatcher.cs:30-46`) already names `projectid` filtering as
the intended extension point. A generalized, gated rule avoids per-event-type
special-casing and is a strict tightening of isolation (the Web already filters
client-side per tab, so no UI regression). The gating (`projectid` present on
both sides) keeps every existing event without `projectid` byte-for-byte
unchanged.

**Alternatives considered:**

- *Opt-in project filter per event type (a whitelist in the dispatcher).* Most
  contained blast radius, but introduces an artificial special-case and a place
  to forget future project-scoped events. Rejected in favor of the documented
  generalized rule, with the blast-radius risk called out below.
- *Rely on client-side filtering only.* Rejected — directly violates the spec's
  no-cross-project-leakage requirement.
- *Direct-to-hub publisher with its own local project filter.* Viable (see D1
  alternative) but duplicates the fanout loop that `EventBridge` already owns.

### D4. The hint is invalidation-only; the inbox HTTP API stays authoritative

The hint payload carries identity, never complete state. On receipt the Web
invalidates `['inbox', projectId]` (the existing TanStack Query key,
`queries.ts:7-10`) and lets the normal query refetch reconcile. The Web never
synthesizes an `InboxItem` from the hint. This makes reconnect / dropped hints
self-healing: the next query (or the next periodic refocus refetch) recovers
truth, satisfying the recovery requirement without any server-side delivery
guarantee.

### D5. Single connection: handle the hint in the central dispatcher

The Web currently runs **two** events connections (`LiveTaskProvider`'s +
`useInboxLiveRefresh`'s). To avoid a third and to fix the source-event-based
false-positives, the inbox hint is added to the global `EVENT_TYPES` subscription
and handled inside `LiveTaskProvider.handleEvent` (the existing central event
switch), which calls a small inbox-effects module:

- invalidate `['inbox', projectId]` (drives both the inbox page and the app-shell
  unread count — see D6);
- evaluate the high-attention notice with duplicate suppression (D7).

`useInboxLiveRefresh` is removed (its second connection and source-event
refetch logic are superseded by the hint).

**Alternative considered:** introduce a shared single-connection events
**provider** (context) so multiple hooks can subscribe to one connection. This
is the cleaner long-term shape but is a larger refactor unrelated to inbox
correctness; deferred. Folding into the existing central dispatcher is the
minimal change that yields a single connection today.

### D6. Live unread count derived from the shared inbox list query

The proposal forbids API changes and there is no unread-count endpoint, so the
app-shell unread count is derived from the same `['inbox', projectId]` list
query the inbox page uses (client-side `list.filter(i => !i.isRead).length`, as
today). A `useUnreadInboxCount(projectId)` selector reads that cached query, so
a single invalidation updates both surfaces. The inbox is a bounded,
low-volume operator feed, so refetching the list per hint is acceptable.

**Alternative considered:** add a dedicated lightweight `GET .../unread-count`
endpoint. Rejected — explicitly disallowed by the proposal's "no API contract
changes" rule.

### D7. High-attention notice with duplicate suppression

For `workflow_failed` and `approval_requested` hints, render an in-app
(toast/inline) notice carrying the issue context (number + kind). Suppression is
evaluated against the **current route at hint time**:

- if the user is viewing the issue the item refers to
  (`/projects/:p/issues/:n` with matching number) → suppress; or
- if the user is on the inbox page (`/projects/:p/inbox`) → suppress (the item
  will appear live via the invalidation, so a toast is redundant).

Otherwise the notice fires once for that hint. `issue_started` /
`issue_completed` are not high-attention and do not toast (per spec).

The suppression uses the hint payload's `issueNumber` (present in the identity
payload from D1) and the router's current location — no extra server round-trip.

## Risks / Trade-offs

- **Generalized dispatcher filter changes routing for all `projectid`-carrying
  events** (not just inbox) → *Mitigation:* the filter is gated on both the
  event carrying `projectid` and the connection declaring a project; events
  without `projectid` are byte-for-byte unchanged. The Web already filters
  client-side per tab, so tighter server isolation is a correctness/efficiency
  improvement, not a regression. Add a regression test asserting a non-`projectid`
  event still reaches project-affinitized connections.
- **Bus re-entrancy: the projection publishes a new event from inside a bus
  handler** → *Mitigation:* the in-process bus tolerates nested dispatch; the
  hint handler (`EventBridge`) only pushes to SignalR and returns. Keep the
  publish after the DB insert and inside the existing try/catch so a publish
  failure cannot corrupt the projection. If re-entrancy ever proves fragile,
  fall back to the D1 direct-to-hub publisher alternative.
- **Project affinity is transport-level, not durable** → by design (matches the
  `project-inbox` spec: subscriptions are transport/presentation state). A
  reconnect re-sends `?projectId=` and re-establishes affinity; missed hints in
  the gap are recovered by D4. Document this so it is not mistaken for a bug.
- **Unread count refetches the whole list per hint** → acceptable for a bounded
  operator inbox; revisit if volume grows (e.g., add a count query later, which
  would then require relaxing the "no API changes" rule in a separate change).
- **Duplicate-suppression is route-based, not item-focus-based** → the inbox
  page has no per-item detail view today, so "viewing the same inbox item" is
  interpreted as "the inbox page is open" (the item appears live there). This
  satisfies the spec scenario; if a per-item detail view is added later, the
  suppression check should narrow to the focused `itemId`.
- **Two Web connections today** → removing `useInboxLiveRefresh` and routing
  through `LiveTaskProvider` must not drop inbox updates for any page that
  mounted the old hook. *Mitigation:* verify all `useInboxLiveRefresh` callers
  are covered by the central dispatcher path; the inbox query invalidation is
  global via the shared query key.

## Migration Plan

This is additive, single-process, local-first — no data migration.

1. **Server (additive, behind the existing build):**
   - add `InboxItemPersisted` to `EventCatalog.ReverseDns`;
   - add per-connection `projectId` to `ConnectionSubscriptionRegistry` and read
     it in `MohistHub.OnConnectedAsync`;
   - add the gated project filter to `UserNotificationDispatcher`;
   - publish the hint in `InboxProjectionHandler` after a non-duplicate insert.
2. **Web (additive, then retire the old hook):**
   - add the type to `REVERSE_DNS_EVENT_TYPES` / `EVENT_TYPES`;
   - handle it in `LiveTaskProvider.handleEvent` (invalidate `['inbox', …]`,
     high-attention toast with suppression);
   - add `useUnreadInboxCount` and surface it in the app shell;
   - remove `useInboxLiveRefresh` and its second connection.
3. **Verify:** `npm test` (server), `npm run typecheck -w packages/web`,
   `npm run test:run -w packages/web`.
4. **Rollback:** revert the commit. Because the hint is invalidation-only and
   the inbox API remains authoritative, rolling back simply returns the UI to
   manual refresh — no data inconsistency, no schema action.

## Open Questions

- Does any existing realtime consumer (beyond the per-project Web tabs) rely on
  receiving `projectid`-carrying events across projects? The codebase audit
  found none (all consumers are per-project via `useProject()`); confirming this
  in review de-risks D3's generalized filter.
- Should the inbox-page live refresh preserve scroll position and avoid a
  visible flash on refetch? (UX polish; not required by spec but worth deciding
  during implementation — TanStack Query's background refetch already avoids a
  loading flash when `stale` data is shown.)
- Notice de-dup across rapid bursts: if two high-attention hints for the same
  issue arrive in quick succession (e.g. failed then re-failed), should the
  second toast be throttled? Spec requires one notice per hint; a small
  per-issue throttle is an implementation polish, not a spec requirement.
