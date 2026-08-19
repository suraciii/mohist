# Agent Subscription Contract

## Boundary

An Agent subscription is the Agent-scoped configuration view of the existing
project routing table. The Server remains the only source of truth for the
subscription data and event matching. The subscription API does not introduce
another matcher, arbitration algorithm, or Slack Connection lifecycle.

`match`, `continue`, and `position` are the routing contract. Clients must
not reconstruct a second filter or priority model. The list is ordered by the
same routing-table position used by dispatch.

## Read states

A resolved Agent always receives an explicit state, including for an empty
list:

- `configured`: at least one subscription exists.
- `empty`: the Agent is active and the list is empty.
- `unconfigured`: the Agent readiness conclusion is `needs-setup`; the list
  remains authoritative and may still be empty or contain saved rules.
- `unavailable`: a Connection exists but is not complete/healthy/enabled, so
  event delivery cannot currently be relied on.
- `no_connection`: the Agent has no non-deleted Connection. Subscription
  configuration remains readable and writes remain local configuration writes;
  the response explains that delivery needs a Connection.

The state never changes a missing Agent into an empty list. An unknown project
or Agent and authentication or authorization failures keep their error status
and API error code. Transport, malformed JSON, and other request failures
remain failures; CLI and Web show an error state rather than an empty list.

## Writes and lifecycle

Create and patch validate the existing routing expression and Agent rules.
Archived Agents reject create and patch with `409` and `agent_archived`;
existing subscriptions can still be listed and deleted. Delete removes only
the addressed routing rule from the active/readable subscription view; `deleted`
is a storage-only tombstone and is never readable, listed, or routed. An
unknown subscription returns `404`; repeating DELETE for the same known id
returns the same `deleted` acknowledgement. Patch is final-state idempotent
when the submitted values already match. Create uses the normal idempotency
key: a repeated request with the same normalized values returns the original
resource and does not create a second row; reusing a key for different values
is a conflict. Without a key, a create retry is a new request and is not
silently deduplicated.

No subscription write starts a runtime, probes Slack, mutates a Connection,
or changes event matching. A missing or unhealthy Connection is observable
status, not a reason to return a fake subscription list.
