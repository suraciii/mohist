# Authentication and Identity

This document defines authentication and attribution for the Mohist control plane. Every access is
attributed to a Principal. A Credential proves identity, a Scope determines permitted operations,
and the authenticated identity becomes the actor recorded on approvals and activities.

Scope: authentication, Scope evaluation, and attribution for Mohist-owned APIs and SignalR hubs.
Identity precedes authorization. The primary invariant is that every request belongs to a
Principal; Scope evaluation is a second-stage capability layered on top, as reflected in the
implementation order under Status. See [`github-integration.md`](github-integration.md) for
identity and credential issuance on external platforms such as GitHub. Slack member access policy
remains within the Connection boundaries defined by [`slack.md`](slack.md) and
[`agent-api.md`](agent-api.md). Multi-user support, roles, permission groups, third-party
application registration, and enterprise identity federation are out of scope.

## Model

### Principal

A Principal is a Server-level resource and is not Project-scoped. There are three kinds:

| Kind | Cardinality | Source | Maximum capability |
|---|---|---|---|
| `admin` | exactly 1 | created during bootstrap | `operator` |
| `service` | one per built-in component | created during bootstrap for local service processes such as the Slack adapter | `operator` |
| `agent` | one per Mohist Agent definition | created with the Agent | no credentials are issued; attribution anchor only |

Fields: `Id`, `Kind`, `Name`, and `CreatedAt`.

The following invariants always hold: there is exactly one admin; no API creates or deletes a
Principal; archiving an Agent does not delete its agent Principal, so historical attribution
records always retain a stable target.

### Credential

A Credential belongs to a Principal. The database stores only the SHA-256 hash of a token. The
token is high-entropy random data and does not need a salt.

Fields: `Id`, `PrincipalId`, `Kind`, `TokenHash`, `Scopes`, `Name`, `ExpiresAt`, `RevokedAt`, and
`CreatedAt`.

| Kind | Purpose | Carrier |
|---|---|---|
| `session` | Web and CLI login session | cookie or local CLI storage |
| `refresh` | CLI session renewal | local CLI storage |
| `pat` | admin-issued personal token for scripts, CI, and external Agents | `MOHIST_TOKEN` environment variable |
| `runner` | Runner machine credential bound to `RunnerId` | local Runner file |
| `integration` | inbound integration token constrained by ProjectId | integration configuration |

Token format: `moh_<kind>_<base64url(32B)>`. The kind prefix supports visual identification and
leak scanning.

**File credentials** are not stored in the database. Admin bootstrap and service credentials use
the existing `OperatorCredential` file mechanism. A missing file is populated with 32 random
bytes, permission mode 0600 is required, symbolic links are rejected, and the value is loaded into
memory for comparison at startup. These credentials are deployment-level roots. Database storage
would create a circular dependency over who initializes the database. The file is the credential;
revocation means replacing its contents.

**Scope** is a closed set:

| Scope | Satisfies |
|---|---|
| `operator` | every route |
| `readonly` | GET only |
| `runner` | `/api/runner/**`, artifact and log reports, and `/hubs/runner` |
| `webhook` | inbound integration endpoints within the Project constrained by the Credential |

Credential Scopes must not exceed the maximum capability of their Principal. `operator` satisfies
every Scope.

Sensitive infrastructure surfaces are not exposed to `readonly` merely because they use GET.
`/api/fs/**`, `/api/logs/tail`, `/api/config/**`, `/api/system/**`, and dead-letter routes always
declare `operator`. `readonly` covers only observation of business resources, including
`/hubs/events` and queries under `/otel/api/**`. Dead-letter routes retain the additional current
constraint that they are mounted only on a loopback-only listener.

### EnrollmentToken

An EnrollmentToken is a one-use registration token with `TokenHash`, `ExpiresAt`, which is 15
minutes after issuance, and `ConsumedAt`. It is not pre-bound to RunnerId. The consumer registers
its own RunnerId.

## Semantics

### Authentication Resolution

Use the first matching carrier in this order:

1. `Authorization: Bearer <token>`.
2. The `mohist_session` cookie for same-origin Web access, which browser WebSockets carry
   automatically.

Tokens are not accepted in query strings. RFC 6750 Section 2.3 and RFC 9700 identify that URIs
enter access logs, browser history, and proxy records. Neither SignalR hub has an exception: Web
uses a same-origin cookie and the Runner SignalR client uses a header.

```text diagram
token FixedTimeEquals each file credential
  -> admin / service Principal
otherwise query Credential by SHA-256(token)
  -> validate RevokedAt and ExpiresAt -> Principal + Scopes
all fail
  -> 401 + WWW-Authenticate: Bearer error="invalid_token" (RFC 6750 Section 3)
  -> do not distinguish missing, expired, or revoked externally
```

Exemptions are `/api/health`; login and device authorization endpoints; Web static assets; GitHub
ingress, which performs its own HMAC validation as defined in
[`github-integration.md`](github-integration.md); and `/otel/v1/*` on the OTLP listener, which
already has a port-isolation boundary. Every other endpoint requires authentication and Scope.

For Scope evaluation, each route declares its required Scope. `readonly` satisfies only GET, and
other Scopes follow the table above. Insufficient Scope returns 403.

### Bootstrap

```text diagram
~/.mohist/admin-token
  absent -> generate and write (0600, reject symlinks)  # admin Principal
~/.mohist/operator-token
  absent -> same                                       # service Principal, existing mechanism
startup
  -> load both as file credentials
```

The `X-Mohist-Operator-Token` header is retired in favor of `Authorization: Bearer` everywhere. The
Slack adapter uses its service credential as a Bearer token. Existing deployments do not need to
rotate because the current operator token file content becomes that service credential.

### Web Login

`POST /api/auth/session {token}` validates an `operator` credential, issues
`Credential(kind=session, 7 days)`, and writes
`Set-Cookie: mohist_session=<token>; HttpOnly; SameSite=Lax; Path=/`. HTTPS requests add `Secure`.
SameSite=Lax combined with a JSON API prevents cross-site forms from carrying the session, so no
separate CSRF token is introduced. Logout revokes the Credential. On a 401 response, the SPA
presents a login page. There is no password; pasting the token logs in.

### CLI Device Authorization (RFC 8628)

Exempt endpoints are `POST /api/auth/device/code` and `POST /api/auth/token`. The confirmation page
at `/device` requires an authenticated Web session. Both endpoints are rate-limited. Polling or
code guessing beyond a small per-source, per-minute allowance returns `slow_down` or 429.

```text diagram
CLI   POST device/code {name}
      -> {device_code, user_code, verification_uri,
          verification_uri_complete, interval=5, expires_in=600}
User  opens verification_uri or verification_uri_complete
      -> enters user_code -> approve -> record approval by admin Principal
CLI   polls POST token
      {grant_type=urn:ietf:params:oauth:grant-type:device_code, device_code}
      <- authorization_pending / slow_down / expired_token
      <- success {access_token(kind=session, 1h), refresh_token(kind=refresh, 30d)}
```

`user_code` has 8 characters from `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`, excluding I, O, 0, and 1 as
in the Slack claim-code precedent. CLI displays it as `XXXX-XXXX`; confirmation input ignores
hyphens and case.

Renewal uses `POST token {grant_type=refresh_token}`. The old refresh token is invalidated
immediately and its hash remains through the end of the window while a new access and refresh pair
is issued through rolling rotation. Reuse of the invalidated refresh token indicates leakage and
revokes the entire session chain derived from that device authorization, following family
revocation in RFC 9700 Section 4.14.2. CLI sessions are stored in
`~/.mohist/credentials.json` with mode 0600.

CLI credential resolution order is `MOHIST_TOKEN` environment variable, then
`credentials.json` matched by Server, then the local `admin-token` file. On 401, CLI first attempts
renewal and prompts the user to run `mo auth login` if renewal fails.

### PAT

`mo auth token create --name <n> --scope operator|readonly [--ttl <hours>]` issues
`Credential(kind=pat)` and returns the complete value exactly once. Every PAT must expire. `--ttl`
defaults to 90 days and is capped at 1 year, following the discipline of GitHub fine-grained PATs;
an unbounded lifetime is not allowed. `--name` is unique among active Credentials for the same
Principal. `revoke` sets `RevokedAt`. `list` displays only the name and prefix, `moh_pat_...`, never
the complete value. This command does not issue integration tokens with `kind=integration`; the
specification for each inbound integration defines its issuance entry point.

### Runner Registration and Credentials

```text diagram
mo install runner (admin authenticated)
  -> POST /api/auth/runner-enrollments -> EnrollmentToken
installer
  -> inject token into Runner environment
Runner first start
  -> POST /api/auth/runner/credentials {enrollment_token, runner_id, hostname}
  -> validate unconsumed and unexpired -> consume
  -> issue Credential(kind=runner, bound to runner_id)
  -> Runner stores $RUNNER_ROOT/credential (0600)
later
  -> Bearer access to /api/runner/** and /hubs/runner
```

The `runnerId` in a path or hub query must equal the `RunnerId` bound to the Credential; otherwise
the request returns 403. This impersonation defense belongs in authentication and no longer trusts
self-assertion. After revocation, every request from that Runner returns 401. Recovery repeats the
registration flow.

### Attribution

After authentication, a mutating handler records the Principal as the actor for the domain action,
including approval `decidedBy`, comment author, and activity records. `--author` remains a display
alias and is no longer an ownership source. Agent attribution retains the existing job and agent
identity reports in the execution protocol. The agent Principal is the stable anchor referenced by
those records.

### Audit Events

Persist records without plaintext tokens for Credential issuance, revocation, and consumption;
EnrollmentToken issuance and consumption; device authorization approval; and Session creation.

## Examples

Local CLI:

```text literal
Server first start -> generate admin-token
mo issue list -> match admin-token file -> admin Principal, operator
```

Remote CI:

```text literal
mo auth token create --name ci --scope readonly --ttl 720h -> moh_pat_... (shown once)
MOHIST_TOKEN=moh_pat_... mo issue list    -> readonly satisfies GET -> 200
MOHIST_TOKEN=moh_pat_... mo issue create  -> insufficient Scope -> 403
```

Runner impersonation defense:

```text literal
Credential is bound to runner-a
POST /api/runner/runner-b/heartbeat with runner-a Credential -> 403
```

## Status

Principal and Credential bootstrap, unified request and SignalR authentication, Web login,
personal access tokens, CLI device authorization, rolling refresh-family protection, Runner
enrollment, Project-constrained integration credentials, Scope enforcement, actor attribution,
and audit events are implemented. The old operator header is retired; local deployment roots use
Bearer authentication through the same resolution boundary as stored credentials.

The implementation keeps authentication and authorization as separate decisions even though both
are now active. That separation preserves the core invariant: Mohist first establishes one trusted
Principal, then decides whether that Principal's Scope can perform the requested capability.
External identity federation and multiple users remain outside this single-administrator model.
