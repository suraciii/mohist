# Issue 555: Direct API activation boundary

## Why

PAT Project grants are already available on `master`, but a grant alone is not
an external Agent API. Registering the seven `/api/v1` route templates before
their handlers, idempotency records, and public projection exist would expose
a versioned surface that can only return `501`. It would make an unavailable
API appear partially shipped and would not meet the response-loss recovery
contract.

## Decision

`/api/v1` is activated only by a vertical implementation slice. The first
mapped route must ship together with its Bearer-PAT boundary, persisted public
projection, concrete handler, and focused tests. A route mapper must not use a
`501` placeholder as a staging mechanism, and the direct-API middleware must
not be registered before it protects a mapped concrete endpoint.

Public observations are derived only from durable canonical AgentJob and
AgentSession facts. Each consumed fact has a stable source identity and a
durable, monotonic revision. The projector commits public snapshots, public
events, source checkpoints, and event-sequence allocation in one transaction.
An API read may inspect canonical source metadata to determine freshness, but
it returns only the persisted public projection. When the required source
revision is ahead of the projection checkpoint, it returns
`503 projection_lag`; it does not compose a response from partial canonical
state or replay an execution.

## Non-goals

- Do not add an `/api/v1` route, middleware registration, schema, handler, or
  compatibility fallback in this change.
- Do not expose a Runner, runtime session, transcript, provider payload,
  workspace, or outbox delivery detail.
- Do not infer a terminal result, retry a command, or advance a canonical
  execution while projection data is behind.
