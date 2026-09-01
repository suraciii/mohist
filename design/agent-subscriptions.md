# Agent Subscription Contract

An Agent subscription is the Agent-scoped view of the existing Project routing
table. The Server remains the only source of truth for subscription data and
event matching. The API adds no matcher, arbitration algorithm, or Slack
Connection lifecycle.

## Design Drivers

- Routing needs one match and priority model. Clients must not reconstruct it.
- Configuration remains readable when delivery is unavailable.
- Read state must distinguish an empty list from an unknown Agent, a missing
  Connection, and an unavailable Connection.
- Writes must preserve routing-rule validation and idempotency without starting
  execution.

## Model

`match`, `continue`, and `position` are the routing contract. The list uses the
same `position` as the Project routing table. A subscription does not own
Agent execution or delivery state.

## Semantics

### Read states

A resolved Agent always receives an explicit state, including for an empty
list:

- `configured`: at least one subscription exists.
- `empty`: the Agent is active and the list is empty.
- `unconfigured`: Agent Readiness is `needs-setup`; the list remains
  authoritative and may be empty or contain saved rules.
- `unavailable`: a Connection exists but is incomplete, unhealthy, or
  disabled, so delivery cannot currently be relied on.
- `no_connection`: the Agent has no non-deleted Connection. Subscription
  configuration remains readable and writes remain local configuration writes;
  the response explains that delivery needs a Connection.

The state never changes a missing Agent into an empty list. Unknown Project or
Agent, authentication, authorization, transport, malformed JSON, and other
request failures retain their error status and API error code. CLI and Web
show an error state instead of an empty list.

### Writes and lifecycle

Create and patch validate the existing routing expression and Agent rules.
Archived Agents reject create and patch with `409` and `agent_archived`.
Existing subscriptions remain listable and deletable.

Delete removes only the addressed routing rule from the active and readable
view. `deleted` is a storage-only tombstone: it is never readable, listed, or
routed. An unknown subscription returns `404`. Repeating DELETE for the same
known ID returns the same `deleted` acknowledgement.

Patch is final-state idempotent when submitted values already match. Create
uses the normal idempotency key: a repeated request with the same normalized
values returns the original resource and does not create another row. Reusing
a key for different values is a conflict. Without a key, a create retry is a
new request and is not silently deduplicated.

No subscription write starts a Runtime, probes Slack, mutates a Connection, or
changes event matching. A missing or unhealthy Connection is observable state,
not a reason to return a fake subscription list.
