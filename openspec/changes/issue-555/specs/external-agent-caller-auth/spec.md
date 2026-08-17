### Requirement: Direct routes accept only a Bearer PAT

Every `/api/v1` External Agent API route SHALL authenticate the caller solely
through the `Authorization: Bearer <PAT>` header. A request without a usable
Bearer PAT, or one presenting the `mohist_session` cookie or a trusted Agent
Connection identity instead of a PAT, MUST be rejected with `401
unauthenticated` and a `WWW-Authenticate: Bearer` challenge. The rejection
MUST NOT distinguish a missing, expired, or revoked token.

#### Scenario: Web cookie session cannot call the direct API

- **WHEN** an authenticated Web cookie session calls `POST /api/v1/projects/{projectId}/agents/{agentId}/launch` without a Bearer PAT
- **THEN** the Server returns `401` with a `WWW-Authenticate: Bearer` challenge
- **AND** no launch, idempotency mapping, or any other effect occurs

#### Scenario: Rejected PAT is not classified

- **WHEN** a direct request presents an expired, revoked, or otherwise invalid PAT
- **THEN** the Server returns `401 unauthenticated` with the Bearer challenge
- **AND** the response does not reveal whether the token was missing, expired, or revoked

### Requirement: Each request resolves an ExternalAgentCaller

Every authenticated direct request MUST resolve the Bearer PAT to the runtime
`ExternalAgentCaller` facts: `callerKeyId` (the Credential ID, stable across
retries and never caller-supplied), the Principal used for attribution, the
granted scopes, and the PAT's persisted direct API Project grant. A PAT whose
grant is absent or empty MUST be denied the entire direct API with `403
forbidden`. `operator_all` MUST be honored only as an explicitly persisted
grant kind and MUST NOT be inferred from `operator` scope.

#### Scenario: Granted PAT resolves its caller identity

- **WHEN** a PAT persisted with an `explicit` grant for `proj_a` authenticates a direct request
- **THEN** the Server resolves that caller's `callerKeyId`, Principal, scopes, and Project grant before any route work begins

#### Scenario: PAT without a grant cannot use the direct API

- **WHEN** an operator-scope PAT created without a Project grant calls any `/api/v1` route
- **THEN** the Server returns `403 forbidden`
- **AND** that PAT remains usable on its existing control-plane surfaces

### Requirement: Route scope is enforced per operation class

Launch, follow-up, and stop writes SHALL require the `operator` scope. Job,
Input, Turn, and per-Session event reads SHALL accept `readonly` or
`operator`. An authenticated caller lacking the required scope MUST receive
`403 forbidden`.

#### Scenario: Readonly PAT cannot write

- **WHEN** a `readonly` PAT calls `POST /api/v1/projects/{projectId}/agent-sessions/{sessionId}/inputs` with a valid body and Idempotency-Key
- **THEN** the Server returns `403 forbidden` before body validation, idempotency lookup, or admission

#### Scenario: Readonly PAT reads public state

- **WHEN** a `readonly` PAT calls `GET /api/v1/projects/{projectId}/agent-jobs/{jobId}`
- **THEN** the Server authorizes the read and returns the public Job observation

### Requirement: Project authorization precedes resource lookup

The selected private Project MUST pass the caller's grant before any resource
lookup, idempotency work, or admission. A Project outside an `explicit` grant
MUST return `403 forbidden` even when that Project does not exist; an
`operator_all` grant covers every private Project of the deployment. The
`404` codes `project_not_found`, `agent_not_found`, `job_not_found`,
`session_not_found`, `input_not_found`, and `turn_not_found` SHALL be returned
only after the grant passes and the requested canonical resource is not
available in that Project. For Agent, Job, Session, Input, and Turn routes,
the resource's canonical Project membership MUST match the selected Project.

#### Scenario: Out-of-grant Project is 403 regardless of existence

- **WHEN** a PAT granted only `proj_a` selects `proj_b`, which does not exist
- **THEN** the Server returns `403 forbidden`, not `404`
- **AND** no resource lookup or idempotency read occurs

#### Scenario: In-grant Project with a foreign Job is 404

- **WHEN** an authorized caller reads a Job that exists but belongs to another Project
- **THEN** the Server returns `404 job_not_found`

### Requirement: 401 and 403 paths have zero side effects

Authentication and authorization failures are terminal and MUST occur before
request validation, normalization, idempotency lookup, and admission. On `401`
or `403` the Server MUST NOT read or return an idempotency mapping, create a
rejection tombstone, reserve a Job, Session, Input, or Turn, write an outbox
item, append a public event, or issue a Runner or provider operation.

#### Scenario: Forbidden launch leaves no trace

- **WHEN** an authenticated caller without the Project grant sends a launch POST carrying an Idempotency-Key that a granted caller previously used
- **THEN** the Server returns `403 forbidden`
- **AND** it neither reads nor returns the other caller's idempotency mapping
- **AND** no Job, Session, Input, Turn, outbox item, or public event is created
