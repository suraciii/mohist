## Why

The project inbox (issue 286) records all four notification kinds for every project, with no way to opt a project out of noisy kinds. An operator who only cares about failures and approvals is forced to tolerate `issue_started`/`issue_completed` noise, and there is no project-level control over what the durable inbox captures. With the inbox projection now in place, a small project-scoped subscription preference lets each project choose which kinds become durable inbox items — preserving the MVP "all on" default while letting projects reduce noise — without touching workflow execution, the runner, or existing inbox items.

## What Changes

- Add a project-scoped **inbox subscription preference** model (`InboxSubscription`) keyed by project, covering the four MVP notification kinds (`workflow_failed`, `approval_requested`, `issue_started`, `issue_completed`) as first-class toggles — not raw CloudEvent type strings.
- **Default = all four kinds enabled** to preserve current MVP behavior. New projects, and projects with no stored preferences, behave as if all four kinds are on.
- Subscription preferences affect **future** inbox projection only. Disabling a kind stops new items of that kind from being created; re-enabling allows them again. **Existing** inbox items are never deleted, rewritten, or marked read when preferences change.
- Add a project-scoped **read/update API** for the preferences (read current enabled/disabled state per kind; update enabled/disabled for the supported kinds).
- The server-side **projection** becomes subscription-gated: it SHALL produce an inbox item for an event only when that kind is enabled for the owning project.
- Add a small **Web UI settings surface** (under project/inbox settings) exposing the four toggles with product-facing labels (e.g. "Workflow failed", "Approval requested"), persisting changes through the API.
- Subscription state is **product subscription state**, separate from SignalR/live connection subscriptions, which remain transport/presentation only.

## Capabilities

### New Capabilities

- `project-inbox-subscription`: The project-scoped inbox subscription preference model and its control surface. Covers the `InboxSubscription` model over the four notification kinds (`workflow_failed`, `approval_requested`, `issue_started`, `issue_completed`); the all-enabled default for projects with no stored preferences; the read/update project-scoped HTTP API; and the Web UI settings surface with product-facing labels. Preferences are product subscription state, separate from realtime connection subscriptions.

### Modified Capabilities

- `project-inbox`: The server-side projection requirement changes from "produce one inbox item per authoritative event by kind, unconditionally" to "produce an inbox item for an event only when that notification kind is enabled for the owning project" (subscription-gated). The current MVP behavior is preserved because the subscription default is all-four-enabled.

## Impact

- **Server** (`packages/server`): new project-scoped subscription preference state (model + durable storage); new read/update HTTP routes; the inbox projection gains a per-kind, per-project subscription gate before inserting an item. Consumes no new events.
- **Web** (`packages/web`): new settings surface (toggles for the four kinds with product labels) plus query/mutation hooks; persists through the subscription API.
- **Runner / workflow engine / issue lifecycle**: no changes. The runner continues to report facts and carries no subscription logic.
- **No breaking changes** to existing API contracts or existing inbox items: current behavior is the default, and preferences affect only future projection.
- **Tests**: projection with a kind enabled, disabled, missing-default (all on), and re-enabled; API read/update of project preferences; UI persistence of the four toggles.
