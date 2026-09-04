### Requirement: PAT Project grant model

A PAT credential SHALL carry exactly one direct-API Project grant: either an `explicit` grant naming a non-empty set of allowed private Projects, or an `operator_all` grant covering all current private Projects owned by the deployment. `operator_all` MUST be persisted explicitly and MUST NOT be inferred from an `operator` scope. A PAT with an absent or empty grant MUST be denied every direct external Agent API route, including PATs issued before Project grants exist. The grant answers only whether the credential may use the direct Agent API for a given private Project; it MUST NOT create reusable Project membership, cross-user visibility, or a general ACL.

#### Scenario: Explicit grant allows only the listed Projects

- **WHEN** a PAT holds an `explicit` grant listing `proj_a` and `proj_b` and the caller addresses a direct Agent API route on either Project
- **THEN** Project authorization SHALL pass for those Projects
- **AND** the same PAT addressing any other Project SHALL be rejected with 403 `forbidden`

#### Scenario: Absent grant denies the direct API

- **WHEN** a PAT issued without a Project grant, or issued before Project grants exist, calls any direct Agent API route
- **THEN** the Server SHALL reject the request with 403 `forbidden`
- **AND** the PAT SHALL remain usable on its existing control-plane surfaces outside the direct Agent API

#### Scenario: Operator scope does not imply all Projects

- **WHEN** a PAT holds `operator` scope but no persisted `operator_all` grant calls a direct Agent API route
- **THEN** the Server MUST NOT treat the operator scope as Project access
- **AND** the request SHALL be rejected with 403 `forbidden` on the Project grant check

### Requirement: ExternalAgentCaller resolution for the direct boundary

Every request to a `/api/v1` direct external Agent route SHALL authenticate with `Authorization: Bearer <PAT>` and the Server SHALL resolve that token to an `ExternalAgentCaller` carrying the Credential's stable `callerKeyId`, the Principal used for attribution, the PAT's route scopes, and its Project grant. The `mohist_session` Web cookie and a trusted Agent Connection identity MUST NOT substitute for the Bearer PAT on any direct route. An absent, invalid, expired, or revoked PAT SHALL return 401 `unauthenticated` with a `WWW-Authenticate: Bearer` challenge that does not distinguish missing, expired, or revoked credentials.

#### Scenario: A valid Bearer PAT resolves the caller

- **WHEN** a request to a `/api/v1` direct route presents a valid, active Bearer PAT
- **THEN** the Server SHALL resolve an `ExternalAgentCaller` bound to that Credential's `callerKeyId`, Principal, scopes, and Project grant
- **AND** every subsequent authorization and idempotency decision for the request SHALL use that resolved caller

#### Scenario: A Web session cookie cannot substitute

- **WHEN** a request to a `/api/v1` direct route carries only the `mohist_session` cookie and no Bearer PAT
- **THEN** the Server SHALL reject the request with 401 `unauthenticated`
- **AND** no `ExternalAgentCaller` SHALL be resolved from the Web session

#### Scenario: An invalid or revoked PAT is indistinguishable

- **WHEN** a direct route receives an expired, revoked, or malformed Bearer token
- **THEN** the Server SHALL return 401 `unauthenticated` with a Bearer challenge
- **AND** the response MUST NOT reveal whether the token was missing, expired, or revoked

### Requirement: Grant-aware PAT issuance

`mo auth token create` SHALL accept `--project <projectId>` (repeatable) and `--all-projects` grant options alongside `--name`, `--scope`, and `--ttl`. Repeated `--project` options SHALL persist an `explicit` grant; `--scope operator --all-projects` SHALL persist an `operator_all` grant. `--project` and `--all-projects` SHALL be mutually exclusive, and `--all-projects` SHALL be invalid without `operator` scope. Issuance SHALL authenticate the issuer and validate the complete private-Project grant before persisting the Credential; a failed binding SHALL return 403 and persist neither the Credential nor its grant. A PAT created without either grant option SHALL remain usable on its existing control-plane surfaces but cannot call the direct Agent API.

#### Scenario: Repeated --project options persist an explicit grant

- **WHEN** an authenticated issuer runs `mo auth token create --name ci --scope operator --project proj_a --project proj_b`
- **THEN** the issued PAT SHALL carry an `explicit` grant whose allowed set is exactly `proj_a` and `proj_b`
- **AND** the full token SHALL be shown exactly once while only its hash is stored

#### Scenario: An operator-wide grant is explicit

- **WHEN** an authenticated issuer runs `mo auth token create --name ci --scope operator --all-projects`
- **THEN** the issued PAT SHALL carry an `operator_all` grant covering all current private Projects

#### Scenario: Invalid grant combinations are rejected before persistence

- **WHEN** issuance is requested with both `--project` and `--all-projects`, or with `--all-projects` under a non-operator scope
- **THEN** the request SHALL be rejected
- **AND** no Credential and no Project grant SHALL be persisted

#### Scenario: Binding to an unknown Project fails atomically

- **WHEN** a grant names a Project that does not resolve as a private Project of the deployment
- **THEN** issuance SHALL return 403 and persist neither the Credential nor its grant
- **AND** no partial credential or grant row SHALL remain

### Requirement: Authorization precedes lookup, idempotency, and admission

For every direct Agent API route the Server SHALL apply this order: authenticate the Bearer PAT and resolve `ExternalAgentCaller`; authorize the required scope and the selected private Project against the grant, and for Agent, Job, Session, Input, and Turn routes also the resource's canonical Project membership; validate route, header, query, and JSON syntax without creating domain state; normalize the allowed write payload and compute its fingerprint; atomically look up the idempotency mapping, then perform canonical admission only when no matching mapping exists. A selected Project outside the PAT grant SHALL return 403 `forbidden` even when that Project does not exist, and only after the grant passes may a missing Project or resource return 404. On 401 or 403 the Server MUST NOT read or return an idempotency mapping, create a rejection tombstone, reserve a Job, Session, Input, or Turn, write an outbox item, append a public event, or issue a Runner or provider operation.

#### Scenario: An out-of-grant Project is 403 before 404

- **WHEN** an authenticated caller requests a direct route whose path selects a Project outside its grant, and that Project does not exist
- **THEN** the Server SHALL return 403 `forbidden` without revealing whether the Project exists
- **AND** no resource lookup, idempotency lookup, or admission SHALL occur

#### Scenario: 401 and 403 have zero side effects

- **WHEN** a direct request fails authentication or authorization
- **THEN** the Server SHALL NOT read or return a request mapping, create a rejection or any canonical record, write an outbox item, append a public event, or contact a Runner
- **AND** the only durable artifact of the request SHALL be its audit log entry

#### Scenario: Missing resources 404 only after the grant passes

- **WHEN** an in-grant caller requests a Job, Session, Input, or Turn that does not exist or does not belong to the selected Project
- **THEN** the Server SHALL return 404 with the matching resource code such as `job_not_found` or `turn_not_found`
- **AND** the 404 MUST NOT be confused with the public execution state `unknown`

### Requirement: Direct-route scope requirements

Launch, follow-up, and stop SHALL require `operator` scope. Job, Input, Turn, and Session event reads SHALL accept `readonly` or `operator` scope. A caller whose PAT lacks the required scope SHALL receive 403 `forbidden` before any resource lookup, idempotency, or admission work, with no side effects.

#### Scenario: A readonly PAT cannot write

- **WHEN** a `readonly` PAT calls launch, follow-up, or stop
- **THEN** the Server SHALL return 403 `forbidden`
- **AND** no request mapping, canonical record, outbox item, or public event SHALL be created

#### Scenario: A readonly PAT can observe

- **WHEN** a `readonly` PAT with a valid Project grant calls the Job, Input, Turn, or Session events route
- **THEN** the read SHALL be authorized and answered from the public projection
