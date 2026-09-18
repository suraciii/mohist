# Web live events

The Web UI needs one project-scoped push connection for domain events,
transcript updates, and workflow task-log deltas. The current module combines
the browser transport with application reconciliation and exposes that
implementation through `shared`. That boundary makes a low-level module own
project, workflow, transcript, and query-cache policy.

## Design Drivers

- `shared` may describe the wire contract, but it must not own application
  lifecycle or domain reconciliation policy.
- One connection must continue to serve all mounted consumers for the selected
  project.
- Reconnect must reconcile authoritative queries before buffered transcript and
  task-log events are delivered.
- Existing admission limits, fencing, retry timing, and malformed-event
  handling remain unchanged.
- Consumers must obtain the live API through the application provider and the
  existing `LiveTaskContext`; widgets do not import an app module.

## System Boundary

```text diagram
shared/api/live-events.ts
  transport contract, wire shapes, and policy-free React context

app/providers/live-events.tsx
  WebSocket lifecycle, subscription state, buffering, reconciliation

app/providers/LiveTaskProvider.tsx
  project selection and domain event routing

widgets/issue-workflow and widgets/session-transcript
  task-log and transcript presentation
```

`shared/api/live-events.ts` exports only the connection status, registration
contracts, wire shapes, and a policy-free context for the API. It does not
create a socket, inspect query keys, or invalidate queries.

`app/providers/live-events.tsx` owns the project URL, WebSocket lifecycle,
reconnection, subscription payload, query invalidation, reconciliation buffers,
and overflow fencing. It is an application service because those rules depend
on the running project and mounted consumers.

`LiveTaskProvider` creates the application service and provides its API through
the shared live-events context alongside the existing issue task context. A
missing project exposes the unavailable API. Widgets use the shared contract
and never import the app provider or its implementation.

## Semantics

The selected project creates one controller. Changing project or unmounting the
provider stops that controller, aborts reconciliation, clears buffers, and
closes the socket. A reconnect sends the current domain, transcript, and
task-log subscriptions. After acknowledgement, observed project queries are
invalidated and registered transcript/task-log queries are refetched. Buffered
events are delivered only after those refetches complete.

Task-log registrations are admitted up to the existing limit. Transcript and
task-log consumers may dispose registrations independently. A malformed event,
reconciliation failure, or buffer overflow cannot block other consumers; the
controller fences the generation and lets the normal reconnect path recover.

## Non-Goals

- This change does not alter the Server event protocol.
- This change does not split domain event routing from `LiveTaskProvider`.
- This change does not add another state store or compatibility export.
- This change does not change polling fallback behavior when task-log scope
  admission is refused.

## Status

The contract split is implemented. Existing controller and DOM tests remain the
acceptance suite for the application provider module.
