## Requirement: Unimplemented direct API routes remain unavailable

The Server MUST NOT expose a future External Agent API route until its
caller-scoped idempotency or public projection owner is durable. Before that
slice lands, the route MUST return `404` for a valid granted Bearer PAT. It
MUST NOT return `501`, accept a body, or invoke canonical Agent execution.

### Scenario: Future write routes are not placeholders

- **GIVEN** a valid operator PAT with a grant for the requested Project
- **WHEN** the caller posts to the launch, follow-up, or stop route shape
- **THEN** the Server returns `404`
- **AND** no Job, Session, Input, Turn, idempotency mapping, queue entry,
  outbox item, or external effect is created

### Scenario: Future read routes are not inferred from internal state

- **GIVEN** a valid granted PAT
- **WHEN** the caller requests an Input, Turn, or Session event route
- **THEN** the Server returns `404`
- **AND** the Server does not serialize a canonical aggregate or internal
  event row as a direct API response

## Requirement: The current Job-only route remains the activation boundary

The mapped Job route MUST continue to authenticate a Bearer PAT, authorize the
canonical Project before Job lookup, and return only the persisted allowlisted
Job projection or `projection_lag`. This guard MUST NOT broaden that response
or reuse the Web/control-plane launch identity as a direct API identity.

### Scenario: A valid grant reaches the concrete Job read

- **GIVEN** a granted PAT and a Job with a persisted public snapshot
- **WHEN** the caller requests the Job route
- **THEN** the Server returns the allowlisted Job snapshot
- **AND** no Session, Input, Turn, Runner, workspace, prompt, or provider field
  is exposed
