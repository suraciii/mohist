---
purpose: "Web UI architectural boundary."
include:
  - "Web UI responsibilities."
  - "API and event consumption boundaries."
  - "Frontend placement rules."
exclude:
  - "Legacy Node/Hono server implementation."
  - "Component file inventory."
  - "Current implementation line references."
---

# Web UI

Web UI 是 Mohist 的本地管理界面。它让用户观察 issue/workflow 状态，并执行审批、启动、暂停、恢复等用户动作。

## Boundary

| Responsibility | Owner |
|----------------|-------|
| Render issue/workflow state | Web UI |
| User actions | Web UI -> API |
| Authoritative state | Server |
| Workflow decisions | WorkflowGrain |
| Runner/process execution | Runner |
| Realtime observation | Server events -> Web UI |

Web UI 不解释 workflow 规则。它只展示 server state，并把用户意图提交给 API。

## Event Model

SSE/live events are observation only.

```text
WorkflowGrain commits event
  -> server persists/publishes
  -> Web UI invalidates or patches queries
```

UI consuming an event must never be required for workflow progress.

## Resource Identity

UI routes may be user-friendly:

```text
/projects/{projectId}/issues/{number}
```

API/query boundary resolves display number to `issueId`. Internal calls and event subjects should prefer:

```text
/projects/{projectId}/issues/{issueId}
/workflow-runs/{workflowRunId}
```

See `design/conventions.md`.

## Placement Rules

- Query hooks own data fetching and cache invalidation.
- Components render state and collect user intent.
- UI state can remember view preferences, filters, selection, and draft input.
- UI state must not be the source of workflow truth.
- Runner details stay behind API payloads; UI should not depend on process implementation.

## Design Preference

Mohist is an operational tool. Prefer dense, scannable screens over marketing-style pages.

Useful first screens:

- issue list / board
- workflow run detail
- approval queue
- runner status

Avoid separate explanatory landing pages inside the app.
