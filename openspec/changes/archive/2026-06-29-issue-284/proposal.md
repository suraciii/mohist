## Why

The durable project inbox (issue #286) only updates when the operator manually
refreshes. While they are elsewhere in the app — or the inbox page is not open —
a workflow failure or approval request lands durably but silently, so the first
sign is often a stalled issue. The authoritative inbox events and a project-scoped
realtime Web path (`MohistHub` / `EventBridge`) already exist, so a lightweight
delivery hint layered on top of the persisted inbox can surface new items live
without touching workflow execution, issue lifecycle, or the runner.

## What Changes

- After an inbox item is persisted, the server SHALL emit a **project-scoped
  realtime "inbox item persisted" hint** to Web sessions subscribed to that
  project. The hint identifies the project and the inbox item; it is a delivery
  nudge, not complete state.
- The browser SHALL treat the hint as **invalidation only**: on receipt it
  re-queries the inbox HTTP API, which remains the source of truth. Realtime
  payloads SHALL NOT be interpreted as full inbox state.
- The **app shell / project navigation** SHALL show a project inbox **unread
  count** that updates live as items arrive or are marked read, without a manual
  refresh.
- The **inbox page** SHALL insert or refresh the new item without a full page
  reload when a hint arrives for the current project.
- **High-attention kinds** (`workflow_failed`, `approval_requested`) SHALL show a
  lightweight in-app notice when the user is not already viewing the relevant
  context (the same issue or the inbox item).
- **Duplicate-notice suppression**: a notice SHALL NOT fire when the user is
  already looking at the same issue or the same inbox item.
- **Strict project isolation**: realtime delivery stays project-scoped. A session
  connected to project A SHALL NOT receive inbox hints for project B.
- **Recovery over transport loss**: browser reconnect, dropped hints, or missed
  events SHALL NOT lose inbox data — the next inbox query reconciles truth.

Non-goals (from the issue): no browser/mobile/email/desktop push, no sound or OS
notification permission prompts, no new multi-user routing, no runner changes,
and no replacement of existing workflow/issue timeline events.

## Capabilities

### New Capabilities

- `project-inbox-realtime`: Realtime delivery hints and the Web realtime
  experience for the durable project inbox. Covers (a) emitting a project-scoped
  "inbox item persisted" hint strictly after persistence over the existing
  Web event path; (b) project-scoped delivery with no cross-project leakage;
(c) the API-authoritative refresh model (hints are invalidation only, the inbox
  HTTP API remains truth, and reconnect/missed hints recover via re-query); (d)
  the app-shell unread count that updates live; (e) live insert/refresh of the
  inbox list without a full reload; (f) lightweight in-app notices for the
  high-attention kinds (`workflow_failed`, `approval_requested`); and (g)
  duplicate-notice suppression when the user is already viewing the relevant
  issue or inbox item.

### Modified Capabilities

_None._ The realtime layer is a new consumer of persisted inbox items and reuses
the existing project-scoped transport hub; it does not change `project-inbox`
requirements. The existing `project-inbox` spec already anticipates this — live
SignalR/dashboard subscriptions remain transport or presentation state and are
explicitly not the source of inbox truth, and the inbox page already sources data
solely through the inbox HTTP API (a realtime hint only triggers that same API
call). No change to `dashboard-attention` (a live, non-durable read surface),
`http-api`, `web-ui`, workflow, issue lifecycle, or runner behavior.

## Impact

- **Server** (`packages/server`): the inbox persistence path (the
  `InboxProjectionHandler` / `InboxStore` insert path) additionally emits a
  project-scoped realtime hint after a successful, non-duplicate insert. Delivery
  reuses the existing `MohistHub` + `EventBridge` + per-connection subscription
  path; the hint carries project and inbox-item identity only. No new workflow or
  issue events; runner untouched.
- **Web** (`packages/web`): subscribe to the new hint on the existing events
  connection; invalidate/refetch the inbox query and unread count; insert/refresh
  items on the inbox page without a full reload; render a lightweight in-app
  notice for `workflow_failed` and `approval_requested` with duplicate
  suppression against the currently-viewed issue/inbox item; surface a live
  unread count in the app shell / project navigation.
- **API / data model**: no inbox HTTP API contract changes and no schema changes
  — the durable `InboxItem` remains the product fact and the API stays
  authoritative.
- **Runner / workflow engine / issue lifecycle**: no changes.
- **Tests**: realtime emission strictly after persistence (and not on duplicate
  inserts), project filtering (no cross-project leakage), unread-count
  invalidation, inbox list live refresh/insert, high-attention notice with
  duplicate-notice suppression, and API recovery after a dropped/reconnected hint.
