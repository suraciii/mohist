## Why

The only "needs attention" surface today is the Dashboard Attention Hero
(`dashboard-attention`), which derives live from the current issue query. The
moment the browser is closed or an issue moves on, the signal is gone: there is
no durable record that a workflow failed, an approval was requested, or work
started/completed while the operator was away. This matters now because the
authoritative issue/workflow events already exist (`WorkflowRunFailed`,
`StageApprovalRequested`, `IssueWorkStarted`, `IssueWorkCompleted`), so a
server-side projection can durably capture them as a first-class project
surface — reliable when the browser is closed — without touching workflow
execution, issue lifecycle, or the runner.

## What Changes

- Add a durable, **project-scoped** inbox on the server. Each `InboxItem`
  belongs to exactly one project and is addressed to that project's local
  operator (no real user id required).
- Inbox items are produced by **server-side projection** from authoritative
  issue/workflow events — never by the Web client or the runner. The runner
  reports facts; events remain the source; projection creates items.
- Record exactly four notification kinds for the MVP: `workflow_failed`,
  `approval_requested`, `issue_started`, `issue_completed`.
- Projection is **idempotent by source event**, so event replay or retry never
  duplicates an item. Items from one project never appear in another project's
  inbox.
- Item text is **product-facing** (e.g. "Issue #42 needs approval", "Issue #42
  workflow failed"), not a raw event type; each item carries enough issue
  identity (issue number + title/summary) to open the issue from the inbox.
- Add a **project inbox HTTP API**: list items (kind, issue number, title or
  summary, creation time, read/unread state), mark one item read, mark all
  project items read, and archive/dismiss one item.
- Add a **Web UI project inbox route/page** with the list, an explicit empty
  state for projects with no items, read/unread presentation, and a link from
  each item back to its issue.
- Existing live SignalR/dashboard subscriptions remain transport/presentation
  state and are **not** the source of inbox truth.

## Capabilities

### New Capabilities

- `project-inbox`: The durable, project-scoped notification inbox surface and
  its server-side projection. Covers the `InboxItem` model and `NotificationKind`
  set (`workflow_failed`, `approval_requested`, `issue_started`,
  `issue_completed`); idempotent-by-source-event projection from authoritative
  issue/workflow events with strict project isolation; product-facing item text
  and issue identity for deep-linking; the project inbox HTTP API (list, mark one
  read, mark all project items read, archive/dismiss); and the Web UI inbox
  route/page (list, empty state, read/unread, link to issue).

### Modified Capabilities

_None._ The inbox is a new consumer of existing authoritative events. It does
not change `workflow-failure-recovery`, the issue lifecycle, runner behavior,
`dashboard-attention` (which remains a live, non-durable read surface),
`http-api`, or `web-ui` requirements.

## Impact

- **Server** (`packages/server`): new inbox projection (event subscriber over
  the existing event stream), an inbox read model, and new project-scoped HTTP
  routes for list / mark-read / mark-all-read / archive. Adds new persistent
  project-scoped inbox state (Orleans grain + storage). Consumes existing
  events only — `WorkflowRunFailed`, `StageApprovalRequested`,
  `IssueWorkStarted`, `IssueWorkCompleted` — and emits no new events.
- **Web** (`packages/web`): new project inbox page/route plus query and
  mutation hooks (list, mark read, mark all read, archive) and an empty state.
- **Runner / workflow engine / issue lifecycle**: no changes. Runner continues
  to report facts; it has no notification-delivery logic.
- **No changes** to existing API contracts or to live dashboard/SignalR
  subscriptions, which remain presentation/transport.
- **Tests**: projection per kind, idempotency under replay, project isolation,
  read-state and archive/dismiss behavior, and the project inbox UI path
  including the empty state.
