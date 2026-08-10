### Requirement: Direct bearer authentication

The `/api/v1` External Agent surface MUST authenticate every request with exactly one valid `Authorization: Bearer <PAT>` credential. A Web session cookie, Agent Connection identity, Runner credential, or caller-supplied caller identifier MUST NOT substitute for the PAT. Tokens in query strings MUST be rejected.

#### Scenario: Missing or invalid bearer token
- **WHEN** a direct API request has no bearer token, has a malformed token, or presents an expired or revoked PAT
- **THEN** the Server returns `401` with a Bearer challenge and a safe unauthenticated error, without distinguishing which credential failure occurred and without creating or reading Agent work

#### Scenario: Cookie or query-string credential
- **WHEN** a direct API request supplies only the `mohist_session` cookie, an Agent Connection identity, or a token in a query parameter
- **THEN** the request is rejected with `401` and no direct API operation is started

### Requirement: Explicit External Agent Project grant

Each PAT used by the direct API MUST resolve to an `ExternalAgentCaller` containing a stable credential identity, Principal identity, granted scopes, and exactly one grant form: a non-empty explicit set of Project IDs or an explicit `operator_all` grant. The deployment-owned authorization catalog MUST contain only current private Projects owned by the deployment. `operator_all` MUST be checked against that catalog at every direct request, so removing a Project from the catalog or changing it to non-private or non-owned MUST deny subsequent access immediately. Explicit grant issuance MUST validate every requested canonical Project ID against the same catalog before the Credential/grant transaction commits; a failed binding MUST return `403` and persist neither row. Explicit grants remain caller-selected ID allowlists after issuance, and canonical resource ownership is still checked at request time. `operator_all` MUST be valid only with `operator` scope. An operator scope without a Project grant MUST NOT authorize direct Agent access, and the grant MUST NOT act as a general Project membership or multi-user ACL.

#### Scenario: Explicit Project grant
- **WHEN** an operator PAT has an explicit grant for Project `proj_a` and calls a direct route for `proj_a`
- **THEN** the Project grant check passes and the request continues to route-scope and canonical resource authorization

#### Scenario: Project outside the grant
- **WHEN** the same PAT calls a direct route for Project `proj_b`
- **THEN** the Server returns `403` before looking up `proj_b`, a resource, or a prior request mapping

#### Scenario: Operator-wide grant validation
- **WHEN** a PAT is issued with `operator_all` and `operator` scope
- **THEN** the PAT can be authorized for each current private Project owned by the deployment; a readonly PAT requesting `operator_all` is rejected before issuance, and a later non-private, non-owned, or removed Project is denied before Project or resource lookup

#### Scenario: Explicit grant binding validation
- **WHEN** an operator requests explicit bindings containing a Project that is unknown, non-private, non-owned, or outside the deployment authorization catalog
- **THEN** the Server returns `403` before persistence and creates neither the Credential nor any External Agent grant row

### Requirement: Grant-aware PAT lifecycle

Operator PAT management MUST support repeated `--project` bindings or an explicit `--all-projects` binding, and MUST reject their simultaneous use. A failed Project binding MUST return `403` and persist neither the credential nor its grant. The full token value MUST be returned only at issuance, persisted only as a hash, and omitted from list, audit, and revoke responses. Revoking a PAT MUST prevent subsequent direct API authentication immediately.

#### Scenario: Failed Project binding
- **WHEN** an operator requests a PAT for a Project that cannot be authorized or does not satisfy the grant rules
- **THEN** the Server returns `403` and no credential or Project grant row is persisted

#### Scenario: Token disclosure and revocation
- **WHEN** an authorized operator creates, lists, and then revokes a PAT
- **THEN** the create response contains the full token once, list responses expose only token metadata and a recognizable prefix, and a later direct API request using the revoked value returns `401`

### Requirement: Scope and authorization ordering

Direct launch, follow-up, and Turn-stop commands MUST require `operator` scope. Job, Input, Turn, and Session-event reads MUST accept `readonly` or `operator` scope. After authenticating the PAT, the Server MUST check the requested Project grant and the canonical Project ownership of the referenced Agent, Job, Session, Input, or Turn before request mapping lookup, body normalization, admission, or external effects.

#### Scenario: Read-only caller attempts a write
- **WHEN** a readonly PAT with a valid Project grant submits a launch, follow-up, or stop command
- **THEN** the Server returns `403` and creates no request mapping, rejection tombstone, Job, Session, Input, Turn, outbox item, or Runner/provider effect

#### Scenario: Authorized missing resource
- **WHEN** an operator or readonly PAT has a valid grant for Project `proj_a` but requests a Job that does not exist in `proj_a`
- **THEN** the Server returns the appropriate `404` resource-not-found error only after the Project grant passes, and the result is not represented as public execution state `unknown`

#### Scenario: Unauthorized Project does not reveal existence
- **WHEN** a PAT without access to `proj_private` submits either a malformed or a replayable request for that Project
- **THEN** the Server returns `403` before parsing the request for admission purposes or reading any matching mapping, and the response has no information about whether the Project or request exists
