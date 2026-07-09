# Web UI

Local management interface. Observe state, execute user actions (approve, start, pause, etc.).

## What belongs where

| Concern | Owner |
|---|---|
| render state | Web UI |
| user actions | Web UI → API |
| authoritative state | Server |
| workflow decisions | WorkflowGrain |
| shell/agent/git execution | Runner |
| realtime push | Server → Web UI |

Web UI never interprets workflow rules. It shows server state and submits user intent.

## Events

Push is observation, not driver. SignalR (`/hubs/events`). UI reconnects → self-reconcile.

```
WorkflowGrain commits → server persists/publishes → SignalR → Web UI refreshes queries
```

## Routes

UI uses friendly paths (`/projects/{pid}/issues/{num}`). API boundary resolves display numbers to ids. Internal calls use `issueId` / `workflowRunId`.

## Rules

- Query hooks own data fetching and cache invalidation.
- UI state stores view prefs, filters, drafts. Never workflow truth.
- Runner details stay behind API. UI never depends on process internals.

## Preference

Dense, scannable screens. No landing pages.

First screens: issue board → workflow detail → approval queue → runner status.
