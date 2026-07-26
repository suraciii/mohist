## Why

The Server retains an unused local git execution surface, leaves an Epic dependency path unguarded, and places domain reactions outside their owning modules. These mismatches obscure the control-plane and runner boundary, make architecture review less reliable, and should be corrected before further workflow work compounds them.

## What Changes

- Remove the unconsumed Server-side git/workspace query service, its dependency-injection registration, and its test fakes; workspace diff, status, commit, and file queries remain runner-provided.
- Document daemon self-management as a Server responsibility so service status, update, and installation-source processes have an explicit exception to runner-owned project execution.
- Include Epic in the architecture dependency guard so its dependencies are checked with the other Server domains.
- Move durable domain event subscription handlers from the shared event infrastructure area into their owning domain modules without changing their subscriptions, trigger timing, or handling semantics.
- Clarify that runner retry classifications are execution facts; Workflow remains the only component that decides whether to retry, recover, or advance work.

## Capabilities
- `server-architecture-alignment`: Server module ownership remains enforceable: runner owns workspace execution and query facts, daemon self-management is explicitly Server-owned, Epic participates in domain dependency checks, and durable domain reactions live with their owning domains while preserving their existing behavior.

## Impact

- **Server:** `Infrastructure/Workspace`, service registration, architecture tests, `SystemInfo`, `Events/Subscriptions`, and the affected Issue, Epic, Workflow, Runner, and notification handler namespaces.
- **Tests:** Remove obsolete git-service fakes and registrations; retain coverage proving workspace API forwarding and event reactions behave unchanged; update architecture assertions for Epic.
- **Documentation:** `design/architecture.md` records daemon self-management and the runner-fact/workflow-decision distinction for retry classification.
- **APIs and dependencies:** No API, CLI, persistence-schema, or dependency changes. Web workspace views and daemon self-management behavior remain unchanged.
