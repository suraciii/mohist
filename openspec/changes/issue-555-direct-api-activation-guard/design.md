# Design: Direct API activation guard

## Current boundary

`GET /api/v1/projects/{projectId}/agent-jobs/{jobId}` is the only mapped
External Agent API route. Authentication resolves a Bearer PAT into an
`ExternalAgentCaller`; the route's Project grant filter runs before the Job
read. The handler reads only the durable Job projection and returns
`projection_lag` when its source revision is not covered.

The control-plane routes under `/api/projects/...` are not direct API routes.
Their launch coordinator remains a separate owner and is not a substitute for
the direct API's caller-scoped idempotency mapping.

## Activation rule

A direct route is mapped only when all of the following are present in the
same slice:

1. Bearer-only authentication and the required scope.
2. Project authorization before resource or idempotency lookup.
3. A durable source of truth for the request identity and its replay result.
4. A durable allowlisted public result, with a source freshness check where
   canonical work can be newer than the projection.
5. Focused tests proving replay, conflict, error ordering, and zero effects.

An unimplemented route is not represented by a `501` handler. The negative
contract intentionally expects `404` for the not-yet-mapped route shapes.
This keeps route availability honest and prevents a client from treating a
request as accepted merely because a URL pattern exists.

## Protected route shapes

| Future operation | Route shape | Required owner before mapping |
| --- | --- | --- |
| launch | `POST /api/v1/projects/{projectId}/agents/{agentId}/launch` | caller-scoped launch mapping plus AgentJob/Session activation |
| follow-up | `POST /api/v1/projects/{projectId}/agent-sessions/{sessionId}/inputs` | caller-scoped input mapping plus Session acceptance |
| stop | `POST /api/v1/projects/{projectId}/agent-turns/{turnId}/stop` | caller-scoped fenced stop mapping |
| Job Input read | `GET /api/v1/projects/{projectId}/agent-inputs/{inputId}` | public execution projection anchored to Input |
| Job Turn read | `GET /api/v1/projects/{projectId}/agent-turns/{turnId}` | public execution projection anchored to Turn |
| Session events | `GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events` | public Session stream, generation, and cursor store |

The guard test sends these requests with a valid granted PAT. This is
important: an authentication failure would only prove the caller was blocked,
not that the route is absent. The test also checks that the request does not
change Job or Session row counts.

## Next implementation seam

The next executable slice should add the public projection tables and their
checkpointed writer. After that, the Job/Input/Turn read route can share one
allowlisted response builder and freshness gate. Launch should follow only
after a direct API idempotency mapping is added with `callerKeyId` in its
scope; adapting the existing Web coordinator without this field would allow
two callers to collide on the same key.
