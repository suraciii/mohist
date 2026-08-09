# Agent Subscription Contract

## Boundary

An Agent subscription is the Agent-scoped configuration view of the existing
project routing table. The Server remains the only source of truth for the
subscription data and event matching. The subscription API does not introduce
another matcher, arbitration algorithm, or Slack Connection lifecycle.

The canonical routes are:

```
GET    /api/projects/{projectRef}/agents/{agentRef}/subscriptions
POST   /api/projects/{projectRef}/agents/{agentRef}/subscriptions
PATCH  /api/projects/{projectRef}/agents/{agentRef}/subscriptions/{id}
DELETE /api/projects/{projectRef}/agents/{agentRef}/subscriptions/{id}
```

The `agentRef` resolution is the existing project-scoped id-or-name resolver.
Every response uses the normal `ApiResponse<T>` envelope. The `data` shape is
the same for CLI and Web consumers:

```json
{
  "subscriptions": [
    {
      "id": "rule_...",
      "projectId": "project_...",
      "agentId": "agent_...",
      "name": "review-failures",
      "match": "event.type == \"com.mohist.workflow.run.failed\"",
      "responsePrompt": "Inspect the failure and report the next action.",
      "continue": false,
      "position": 1,
      "status": "active",
      "createdAt": "2026-08-09T00:00:00.0000000+00:00",
      "updatedAt": "2026-08-09T00:00:00.0000000+00:00"
    }
  ],
  "state": "configured",
  "agentStatus": "active",
  "readiness": "Ready",
  "connection": "connected"
}
```

`match`, `continue`, and `position` are the routing contract. Clients must
not reconstruct a second filter or priority model. The list is ordered by the
same routing-table position used by dispatch. A write response returns the
same `AgentSubscriptionDto` item shape, not a client-specific projection.

## Read states

The list route returns `200` for a resolved Agent, including an empty list. The
state is explicit:

- `configured`: at least one subscription exists.
- `empty`: the Agent is active and the list is empty.
- `unconfigured`: the Agent readiness conclusion is `Needs setup`; the list
  remains authoritative and may still be empty or contain saved rules.
- `unavailable`: a Connection exists but is not complete/healthy/enabled, so
  event delivery cannot currently be relied on.
- `no_connection`: the Agent has no non-deleted Connection. Subscription
  configuration remains readable and writes remain local configuration writes;
  the response explains that delivery needs a Connection.

The state never changes a missing Agent into an empty list. An unknown
project/Agent is `404`. Authentication and authorization failures preserve
their `401`/`403` status and API error code. Transport, malformed JSON, and
other request failures remain failures; CLI and Web show an error state rather
than an empty list.

## Writes and lifecycle

Create and patch validate the existing routing expression and Agent rules.
Archived Agents reject create and patch with `409` and `agent_archived`;
existing subscriptions can still be listed and deleted. Delete removes only
the addressed routing row. Patch is final-state idempotent when the submitted
values already match. Delete returns the same deletion acknowledgement when a
client repeats it for the same id. Create uses the normal idempotency key: a
repeated request with the same key returns the original resource and does not
create a second row; reusing a key for different values is a conflict. Without
a key, a create retry is a new request and is not silently deduplicated.

No subscription write starts a runtime, probes Slack, mutates a Connection,
or changes event matching. A missing or unhealthy Connection is observable
status, not a reason to return a fake subscription list.
