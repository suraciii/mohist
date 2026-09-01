# Authentication and Identity

Mohist keeps local use frictionless while assigning a trusted identity to every
remote effect. One Administrator owns a self-hosted deployment. CLI clients,
Runners, integrations, and External Agents use credentials that can be revoked
independently.

This design covers authentication, authorization, and attribution for
Mohist-owned APIs and WebSocket endpoints. GitHub verifies its own ingress in
[github-integration.md](github-integration.md). Slack member policy belongs to
the Agent Connection boundary in [slack.md](slack.md). Direct External Agent
access builds on this model and is defined further in [agent-api.md](agent-api.md).

## Design Drivers

- Local commands do not require an interactive login. Access to the Server host
  is the local trust boundary.
- Browser, remote CLI, service, Runner, integration, and direct Agent callers
  use separate credentials. Each credential has bounded lifetime and blast
  radius.
- Authentication establishes one stable Principal before authorization or
  domain work. Activity and Approval attribution therefore remain trustworthy.
- Direct External Agent callers need explicit access to private Projects. An
  operator Scope does not silently grant every Project.
- Authentication and Project authorization precede resource lookup,
  idempotency, and admission. Failures must not reveal private resources or
  prior idempotent requests.
- Project grants for direct Agent PATs are credential boundaries. They are not
  roles, memberships, or a general ACL.

## Model

### Principal

A Principal is the stable identity recorded as the actor of an operation. It is
a Server resource, not a Project membership. Mohist has three Principal kinds:

- `admin`: exactly one Principal for the self-hosted deployment owner.
- `service`: one Principal for each built-in component.
- `agent`: one Principal for each Mohist Agent, preserving attribution without
  giving the Agent a login credential.

Mohist does not expose Principal creation or deletion. Archiving an Agent does
not delete its Principal because historical attribution remains stable.

### Credential

A Credential proves a Principal's identity. Persistence stores only a SHA-256
token hash. It never stores the token. Tokens use high-entropy random data, so
the hash protects the secret without a password-style salt or recovery path.
The five kinds are:

- `session`: short-lived Web or CLI access.
- `refresh`: CLI renewal with rolling family revocation.
- `pat`: headless scripts, CI, and External Agents.
- `runner`: one Runner identity and the Runner-only surface.
- `integration`: one inbound integration constrained to a Project.

Issued tokens use `moh_<kind>_<base64url(32B)>`. The prefix supports leak
scanning and human recognition. It is not an authorization input.

Administrator bootstrap and built-in service roots remain file credentials. They
cannot depend on the database because they authorize the process that
initializes it. Mohist creates a missing root with 32 random bytes, requires
mode 0600, rejects symbolic links, and loads the value at startup. Replacing a
file value revokes that deployment root.

### Scope

Scope limits the class of operation a credential can perform:

- `operator`: all control-plane capabilities.
- `readonly`: observation of business resources.
- `runner`: Runner work, artifacts, logs, heartbeat, and connection operations.
- `webhook`: one constrained inbound integration within a Project.

`operator` satisfies every Scope, but a Credential cannot exceed its Principal's
maximum capability. `readonly` does not gain infrastructure or secret-bearing
access merely because an operation is a read. Filesystem, configuration,
system, log-tail, and dead-letter operations remain operator-only. Dead-letter
operations also remain loopback-only.

### ExternalAgentCaller

A Bearer PAT at the direct External Agent boundary resolves to an
`ExternalAgentCaller`. It gives authorization and idempotency one stable caller
identity without exposing or persisting the plaintext token.

- `callerKeyId` is the Credential ID. The caller never supplies or receives it.
- `principalId` is the attribution Principal.
- `scopes` are the route capabilities granted to the PAT.
- `projectGrant` is `explicit` or `operator_all`.
- `allowedProjectIds` is a non-empty Project set for an `explicit` grant.

`operator_all` explicitly grants all current private Projects. It is not inferred
from `operator` Scope. An `explicit` grant allows only its listed Projects. An
empty or absent grant denies the direct Agent API, including for older PATs.

The grant answers whether the credential may use the direct Agent API for a
private Project. It does not create Project membership, cross-user visibility,
or RBAC.

### EnrollmentToken

Runner installation uses an EnrollmentToken as a short bridge from Administrator
authorization to a machine credential. It is single-use, expires 15 minutes
after issuance, and is not bound to a Runner ID in advance. The first valid
consumer registers its Runner identity and exchanges the token for a bound
Runner Credential.

## Authentication Boundary

Mohist accepts the first valid carrier in this order:

1. `Authorization: Bearer <token>`.
2. `mohist_session` for same-origin Web access.

Tokens are never accepted in a query string because URLs enter browser history,
access logs, and proxy records. The Web event socket uses the same-origin
cookie. Runner control connections use the authorization header. A Runner
Credential must match the claimed Runner identity. `operator` still satisfies
the `runner` Scope. Moving Runner control from SignalR to WebSocket does not
change this boundary.

A presented credential resolves in this order:

1. Compare deployment-root credentials in constant time.
2. Hash the token and resolve a stored Credential.
3. Reject expired or revoked credentials.
4. Resolve the Principal and Scope.
5. For a direct Agent PAT, resolve `ExternalAgentCaller` and its Project grant.
6. Authorize the requested capability.
7. Begin resource lookup and domain work.

Invalid authentication returns 401 with a Bearer challenge. It does not reveal
whether a token is missing, expired, or revoked. Insufficient Scope returns 403.
Health, authentication entry points, Web static assets, GitHub's
signature-verified ingress, and the isolated OTLP listener are the closed
unauthenticated set.

The authentication flow uses one Principal before authorization:

```text diagram
+--------------------+   +------------------+    +-------------------+
| credential carrier +---| direct Agent PAT |----| platform identity |-+
+--------------------+   +---------+--------+    +---------+---------+ |
                  +----------------+    +------------------+           |
                  v                     v                              |
          +---------------+   +-------------------+                    |
          | Project grant |   | Connection policy +--------------------++
          +-------+-------+   +-------------------+                    ||
                  +----------+                                         ||
                             v                                         ||
                       +-----------+                                   ||
                       | Principal |<----------------------------------+|
                       +-----+-----+                                    |
                             |                                          |
                             v                                          |
                         +-------+                                      |
                         | Scope |                                      |
                         +---+---+                                      |
                             |                                          |
                             v                                          |
                    +----------------+                                  |
                    | product action |<---------------------------------+
                    +----------------+
```

## Direct External Agent Authorization

Direct External Agent calls accept a Bearer PAT only. A Web cookie or trusted
Agent Connection identity belongs to another adapter and cannot substitute for
the caller key.

Writes that launch, continue, or stop work require `operator`. Public reads may
use `readonly` or `operator`. Every request follows this order:

1. Authenticate the Bearer PAT.
2. Resolve `ExternalAgentCaller`.
3. Authorize Scope.
4. Authorize the Project grant.
5. Check canonical resource ownership.
6. Validate and normalize the request.
7. Look up idempotency and compare its fingerprint.
8. Admit the request and apply durable effects.

A Project outside the grant returns 403 even when it does not exist. Only after
the grant passes may a missing Project or resource return 404. This prevents
status codes or idempotency mappings from becoming a private-resource oracle.

On 401 or 403, Mohist does not read or return a request mapping, create a
rejection, Job, Session, Input, Turn, outbox record, or public event, or contact
a Runner. The complete retry, public projection, cursor, and stop contracts
belong to [agent-api.md](agent-api.md).

## Human Access

### Local Bootstrap

On first Server start, Mohist creates the Administrator and built-in service
credential files when absent. Local `mo` discovers the Administrator file
automatically. Every request still resolves to a Principal.

### Web Login

The Web UI exchanges an Administrator token for a seven-day HttpOnly,
SameSite=Lax session cookie. HTTPS adds `Secure`. Logout revokes the session.
Same-site cookies and the JSON-only API prevent cross-site forms from creating
authenticated effects without a separate CSRF token.

### Remote CLI

Remote CLI uses RFC 8628 device authorization. The Administrator secret stays
off the remote machine, and an authenticated Web UI approves the login. Device
codes expire after ten minutes. Polling and guessing are rate-limited.

The access token lasts one hour. A refresh token lasts 30 days and rotates on
every use. Reuse of an invalidated refresh token revokes its session family
because reuse indicates likely leakage. CLI resolves credentials in this order:
`MOHIST_TOKEN`, the Server-matched local session, and the Administrator file on
the Server host.

## Personal Access Tokens

PATs support callers that cannot complete a browser flow. The token is shown in
full once. It expires by default after 90 days and never after more than one
year. Active names are unique per Principal. Revocation is immediate. Listing
shows only a recognizable prefix.

The issuance contract is:

```text literal
mo auth token create --name <name> --scope operator|readonly [--ttl <hours>]
  [--project <projectId>]... [--all-projects]
```

A PAT for the direct Agent API must choose exactly one Project grant form:

- Repeated `--project` options persist an `explicit` grant.
- `--scope operator --all-projects` persists `operator_all`.
- `--project` and `--all-projects` are mutually exclusive.
- `--all-projects` is invalid without `operator` Scope.
- A PAT without either grant remains usable on existing control-plane surfaces
  but cannot call the direct Agent API.

Issuance authenticates the issuer and validates the complete private-Project
grant before persisting the Credential. A failed binding returns 403 and
persists neither the Credential nor its grant. PAT use repeats Scope and Project
authorization before idempotency or admission. Issuance-time validation is not
a substitute for request-time authorization.

## Machine Access

### Runner Registration

`mo install runner` obtains a one-use EnrollmentToken. On first start, Runner
exchanges it for a Credential bound to its Runner ID and stores that Credential
with mode 0600. Every later report and connection uses it. An asserted Runner ID
that differs from the binding returns 403. Revocation requires new enrollment.

### Integrations and Services

Each inbound integration uses a distinct Project-constrained Credential. A leak
cannot authorize another integration. GitHub uses its native HMAC signature.
Built-in local adapters use service Credentials and keep their external identity
and access-policy checks.

## Attribution and Audit

After authentication, mutating operations record the Principal as actor. A
display alias such as `--display-name` never proves ownership. Agent execution
continues to attribute work to the Agent Principal.

Audit records contain no plaintext token. They cover Credential issuance,
revocation, and consumption; Runner enrollment issuance and consumption; device
authorization approval; and Session creation.

## Non-Goals

- Multiple Mohist users, roles, permission groups, or reusable Project ACLs.
- Third-party application registration or a public developer platform.
- Single sign-on, external OIDC, or enterprise identity federation.

## Status

Principal and Credential bootstrap, unified API and WebSocket authentication, Web
login, CLI device authorization and refresh-family protection, personal access
tokens, persisted direct API Project grants, Runner enrollment,
Project-constrained integration credentials, Scope enforcement, actor
attribution, and audit records are implemented.

The direct External Agent boundary is shipped. Bearer PAT requests resolve to
`ExternalAgentCaller`, and `/api/v1` enforces the persisted Project grant and
route Scope before resource lookup, idempotency, or admission. PATs without a
direct API grant retain existing control-plane behavior but cannot use the direct
API. The public route and observation contract is defined in
[agent-api.md](agent-api.md).
