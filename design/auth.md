---
status: shipped
---

# Authentication and Identity

Authentication in Mohist must preserve two properties that are easy to lose in
a self-hosted system: local use stays frictionless, and every remote effect has
a trusted identity. One administrator owns the deployment, while CLI clients,
Runners, integrations, and external Agents receive credentials that can be
revoked independently.

This design covers authentication, authorization, and attribution for
Mohist-owned APIs and SignalR hubs. GitHub verifies its own ingress as described
in [github-integration.md](github-integration.md). Slack member policy remains
inside the Agent Connection boundary in [slack.md](slack.md). The direct
external Agent boundary builds on this model and is defined in
[agent-api.md](agent-api.md).

## Drivers

- Local commands should not require an interactive login. Access to the Server
  host is already the local trust boundary.
- Browser, remote CLI, service, Runner, integration, and direct Agent callers
  must not share one long-lived secret. A leaked credential must have a bounded
  lifetime and blast radius.
- Authentication must establish one stable Principal before authorization or
  domain work begins. This makes Activity and Approval attribution trustworthy.
- A direct external Agent caller must receive explicit access to private
  Projects. An operator Scope alone must not silently become access to every
  Project.
- Authorization failures must not reveal whether a private resource or a prior
  idempotent request exists. Authentication and Project authorization therefore
  precede resource lookup, idempotency, and admission.
- The model must stay smaller than a multi-user identity system. Project grants
  for direct Agent PATs are credential boundaries, not roles, memberships, or a
  general ACL.

```text diagram
local administrator file -----------+
browser or remote CLI session ------+--> Principal --> Scope --> product action
service or Runner credential -------+
integration credential -------------+
direct Agent PAT --> Project grant -+

external platform identity --> Connection or ingress policy --> product action
```

## Identity Model

### Principal

A Principal is the stable identity recorded for an action. It is a Server-level
resource rather than a Project membership.

| Kind | Cardinality | Why it exists |
|---|---|---|
| `admin` | exactly one | Represents the owner of the self-hosted deployment. |
| `service` | one per built-in component | Separates trusted local machines and adapters from the administrator. |
| `agent` | one per Mohist Agent | Preserves Agent attribution without giving the Agent a login credential. |

Mohist does not expose Principal creation or deletion. Archiving an Agent does
not delete its Principal because historical attribution must remain stable.

### Credential

A Credential proves a Principal's identity. Persisted credentials store only a
SHA-256 token hash, never the token value. The token uses high-entropy random
data, so hashing protects the secret without introducing a password-style salt
or recovery path.

| Kind | Trust boundary |
|---|---|
| `session` | Short-lived Web or CLI access. |
| `refresh` | CLI session renewal with rolling family revocation. |
| `pat` | Headless callers such as scripts, CI, and external Agents. |
| `runner` | One Runner identity and the Runner-only surface. |
| `integration` | One inbound integration constrained to a Project. |

Issued tokens use `moh_<kind>_<base64url(32B)>`. The prefix supports human
recognition and leak scanning; it is not an authorization input.

Administrator bootstrap and built-in service roots remain file credentials.
They cannot depend on the database because they authorize the process that
initializes it. Mohist creates a missing root with 32 random bytes, requires
mode 0600, rejects symbolic links, and loads the value at startup. Replacing the
file value revokes that deployment root.

### Scope

Scope limits which class of operation a credential can perform.

| Scope | Capability |
|---|---|
| `operator` | All control-plane capabilities. |
| `readonly` | Observation of business resources only. |
| `runner` | Runner work, artifact, log, heartbeat, and connection capabilities. |
| `webhook` | One inbound integration within its constrained Project. |

`operator` satisfies every Scope. A Credential cannot exceed the maximum
capability of its Principal. `readonly` does not gain access to infrastructure
or secret-bearing surfaces merely because an operation is a read. Filesystem,
configuration, system, log-tail, and dead-letter operations remain
operator-only; dead-letter operations also retain their loopback-only listener
boundary.

### ExternalAgentCaller

A Bearer PAT used at the direct external Agent boundary resolves to an
`ExternalAgentCaller`. This model gives authorization and idempotency one stable
caller identity without exposing or persisting the plaintext token.

| Fact | Contract |
|---|---|
| `callerKeyId` | The Credential ID. It is stable across retries and never supplied or returned by the caller. |
| `principalId` | The Principal used for attribution. |
| `scopes` | The route capabilities granted to the PAT. |
| `projectGrant` | Either `explicit` or `operator_all`. |
| `allowedProjectIds` | A non-empty Project set when the grant is `explicit`. |

`operator_all` is an explicit grant to all current private Projects owned by the
deployment. It is not inferred from `operator` Scope. An `explicit` grant allows
only the listed Projects. An empty or absent grant denies the direct Agent API,
including for PATs issued before Project grants exist.

The grant answers one question: may this credential use the direct Agent API for
this private Project? It does not create reusable Project membership,
cross-user visibility, or RBAC.

### EnrollmentToken

A Runner installation needs a short bridge from administrator authorization to
a distinct machine credential. An EnrollmentToken is single-use, expires 15
minutes after issuance, and is not pre-bound to a Runner ID. The first valid
consumer registers its Runner identity and exchanges the token for a bound
Runner credential.

## Authentication Boundary

Mohist accepts the first valid carrier in this order:

1. `Authorization: Bearer <token>`.
2. The `mohist_session` cookie for same-origin Web access.

Tokens are never accepted in a query string because URIs enter browser history,
access logs, and proxy records. Web SignalR uses the same-origin cookie. Runner
SignalR uses the authorization header.

```text diagram
present credential
  -> compare deployment root credentials in constant time
  -> otherwise hash token and resolve a stored Credential
  -> reject expired or revoked credentials
  -> resolve Principal and Scope
  -> for a direct Agent PAT, also resolve ExternalAgentCaller and Project grant
  -> authorize the requested capability
  -> begin resource lookup and domain work
```

Invalid authentication returns 401 with the Bearer challenge and does not reveal
whether a token was missing, expired, or revoked. Insufficient Scope returns
403. Health, authentication entry points, Web static assets, GitHub's
signature-verified ingress, and the isolated OTLP listener form the closed
unauthenticated set.

## Direct External Agent Authorization

Direct external Agent calls accept a Bearer PAT only. A Web cookie and a trusted
Agent Connection identity belong to different adapters and cannot substitute
for that caller key.

Writes that launch, continue, or stop work require `operator`. Public reads may
use `readonly` or `operator`. Every request follows one security order:

```text diagram
Bearer PAT authentication
  -> ExternalAgentCaller resolution
  -> Scope authorization
  -> Project grant authorization
  -> canonical resource ownership check
  -> request validation and normalization
  -> idempotency lookup and fingerprint comparison
  -> admission and durable effects
```

A Project outside the grant returns 403 even when that Project does not exist.
Only after the grant passes may a missing Project or resource return 404. This
ordering prevents a credential from using status codes or idempotency mappings
as an oracle for private resources.

On 401 or 403, Mohist does not read or return a request mapping, create a
rejection, Job, Session, Input, Turn, outbox record, or public event, or contact
a Runner. The complete retry, public projection, cursor, and stop contracts are
owned by [agent-api.md](agent-api.md).

## Human Access

### Local Bootstrap

On first Server start, Mohist creates the administrator credential file and the
built-in service credential file when absent. Local `mo` discovers the
administrator file automatically. This preserves zero-login local use while
still requiring every request to resolve to a Principal.

The retired `X-Mohist-Operator-Token` header is not a parallel credential path.
All callers use Bearer authentication or the Web session cookie. Existing
operator file content becomes the service credential, so this unification does
not require a deployment-wide secret rotation.

### Web Login

The Web UI exchanges an administrator-level token for a seven-day, HttpOnly,
SameSite=Lax session cookie. HTTPS adds `Secure`. Logout revokes the session.
Same-site cookies and the JSON-only API prevent cross-site forms from creating
authenticated effects without adding a separate CSRF token.

### Remote CLI

Remote CLI uses RFC 8628 device authorization because it keeps the administrator
secret out of the remote machine and lets the already authenticated Web UI
approve the login. Device codes expire after ten minutes and rate limits protect
the polling and guessing surfaces.

The resulting access token lasts one hour. A refresh token lasts 30 days and is
rotated on every use. Reuse of an invalidated refresh token revokes its entire
session family because reuse indicates likely credential leakage. CLI resolves
credentials in this order: `MOHIST_TOKEN`, the Server-matched local session, and
the administrator file on the Server host.

## Personal Access Tokens

PATs support callers that cannot complete a browser flow. The token is shown in
full once, must expire, defaults to 90 days, and cannot exceed one year. Active
names are unique per Principal. Revocation is immediate; listing exposes only a
recognizable prefix.

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
- A PAT without either grant remains usable on its existing control-plane
  surfaces but cannot call the direct Agent API.

Issuance authenticates the issuer and validates the complete private-Project
grant before it persists the Credential. Any failed binding returns 403 and
persists neither the Credential nor its grant. Use of the PAT repeats Scope and
Project authorization before idempotency or admission; issuance-time validation
is not a permanent substitute for request-time authorization.

## Machine Access

### Runner Registration

`mo install runner` obtains a one-use EnrollmentToken. On first start, Runner
exchanges it for a credential bound to its Runner ID and stores that credential
with mode 0600. Every later Runner report and connection uses that credential.
The asserted Runner ID must match the credential binding; a mismatch returns
403. Recovery after revocation repeats enrollment rather than reusing the old
secret.

### Integrations and Services

Each inbound integration uses a distinct Project-constrained credential so one
leak cannot authorize another integration. GitHub uses its native HMAC signature
instead. Built-in local adapters use service credentials and retain their own
external identity and access-policy checks.

## Attribution and Audit

After authentication, mutating operations record the Principal as their actor.
Display aliases such as `--author` never become ownership evidence. Agent
execution continues to attribute work to the Agent Principal.

Audit records contain no plaintext token. They cover credential issuance,
revocation, and consumption; Runner enrollment issuance and consumption; device
authorization approval; and session creation.

## Non-goals

- Multiple Mohist users, roles, permission groups, or reusable Project ACLs.
- Third-party application registration or a public developer platform.
- Single sign-on, external OIDC, or enterprise identity federation.

## Status

Principal and Credential bootstrap, unified API and SignalR authentication, Web
login, CLI device authorization and refresh-family protection, personal access
tokens, persisted direct API Project grants, Runner enrollment,
Project-constrained integration credentials, Scope enforcement, actor
attribution, and audit records are implemented.

The direct external Agent boundary is also shipped. Bearer PAT requests resolve
to `ExternalAgentCaller`, and the `/api/v1` boundary enforces the persisted
Project grant and route Scope before resource lookup, idempotency, or admission.
PATs without a direct API grant retain their existing control-plane behavior but
cannot use the direct API. The public route and observation contract is defined
in [agent-api.md](agent-api.md).
