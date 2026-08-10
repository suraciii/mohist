## Context

Issue 555 adds a private, headless External Agent API for callers that cannot
depend on a browser session, a Runner identity, SignalR delivery, or an
in-process response. The proposal and the four capability specifications define
the public contract; this document defines the server-side implementation
boundary.

The repository already has the important execution authorities. Manual launch
uses `IAgentLauncher` and `AgentLaunchCoordinatorGrain`; follow-up is accepted
by `IAgentSessionGrain`; stop uses the existing Session/Runner control path;
`AgentJob` and `AgentSession` persist their own facts and CloudEvent outboxes.
The current HTTP routes, however, use `/api/projects`, generic authentication,
internal-shaped DTOs, and inconsistent idempotency scopes. `AuthResolutionMiddleware`
also permits the Web session cookie on the general API, while PAT persistence
currently has only a single integration-style `ProjectId` constraint. The
existing `AgentSessionEvents` table is durable, but it is an internal aggregate
event store with a dispatch flag, not a public projection with opaque cursors,
retention, or privacy filtering.

The implementation must therefore add a stable adapter without creating a
second Agent execution lifecycle. Canonical Job, Session, Input, Turn,
admission, stop-fence, and terminal facts remain owned by the existing domain
and Orleans grains. The new boundary must work with separate aggregate
transactions, injectable `TimeProvider`, the existing EF Core persistence, and
the existing CloudEvent/outbox infrastructure. No new external dependency is
required.

## Goals / Non-Goals

**Goals:**

- Expose the versioned `/api/v1` launch, follow-up, read, event-replay, and
  Turn-stop routes with stable public contracts.
- Resolve a Bearer PAT into a credential-bound `ExternalAgentCaller`, enforce
  its scope and Project grant before resource lookup, body admission, or
  idempotency reconciliation.
- Make every write recoverable after a lost response through a durable,
  caller-isolated request mapping and server-computed fingerprint.
- Adapt the existing AgentJob/AgentSession lifecycle instead of duplicating
  execution, queueing, Runner binding, or transcript ownership.
- Store and serve an allowlisted public execution snapshot and per-Session
  public event journal with projection checkpoints and exclusive cursors.
- Make unresolved external effects observable as `unknown` without replaying
  work, and make stop/terminal races converge through one terminal fence.
- Extend PAT issuance, listing, revocation, CLI options, migrations, and tests
  to represent explicit Project grants and `operator_all`.

**Non-Goals:**

- General user management, reusable Project ACLs, RBAC, OAuth client
  registration, OIDC, or a public multi-tenant developer platform.
- Replacing or changing the existing Web, CLI, Agent Connection, Runner, or
  internal Session routes.
- Exposing prompts, transcripts, Runtime Sessions, Runner identities,
  workspace paths, operation/fence identifiers, provider payloads, or raw
  internal CloudEvents.
- Moving canonical execution state into the public projection or allowing an
  external caller to select a Runtime, Runner, model, workspace, instructions,
  Skills, or provider operation.
- Automatically retrying an effect because a caller polls, reconnects, or
  observes `unknown`.

## Decisions

### 1. Add a dedicated versioned adapter and keep internal routes unchanged

Add an External Agent route module under `/api/v1` and separate application
services for authorization, command admission, public mapping, and event reads.
Each endpoint accepts canonical IDs and calls an explicit public-contract
mapper; it does not serialize `AgentJobLaunchRead`, `AgentSessionRead`,
`AgentSessionFollowupResult`, or stop-operation DTOs.

The route handlers will use the canonical project ID directly. They will not
use the existing display-name `ProjectResolutionEndpointFilter`, because an
unauthorized Project must be rejected before resolving its existence. Resource
authorization will verify that an Agent, Job, Session, Input, or Turn belongs to
the selected Project before the handler parses a write body for admission.

The alternative was to add flags and new response branches to the existing
`/api/projects` routes. That would preserve less code initially, but it would
inherit cookie authentication, attachment/context behavior, internal fields,
and incompatible idempotency semantics. A separate adapter keeps the v1
contract narrow and makes accidental export of an internal DTO testable.

### 2. Model External Agent grants separately from integration constraints

Extend PAT issuance with a durable external grant model rather than reusing
`Credential.ProjectId` or `ScopesJson`:

- one grant row identifies the PAT and has kind `explicit` or `operator_all`;
- explicit grants have one child row per canonical Project ID;
- the grant has exactly one form, and explicit grants cannot be empty;
- the existing integration `ProjectId` column remains owned by integration
  credentials and keeps its current meaning.

`ICredentialStore` will create the Credential and its grant in one database
transaction after authenticating the issuer and validating every requested
Project. The store will return the full PAT only on successful issuance. List
and revoke responses expose grant metadata and the existing token prefix, never
the token value. Request-time resolution loads the PAT by hash and builds an
`ExternalAgentCaller` containing `callerKeyId`, Principal ID, scopes, and the
grant.

For `/api/v1`, `AuthResolutionMiddleware` will require exactly one Bearer
header, reject cookies, query-string tokens, file credentials, and non-PAT
credentials, and perform the route scope check. A small
`ExternalAgentAuthorization` service will perform the grant check from the
canonical route Project ID without first loading that Project. Only after the
grant passes may the endpoint query the Project or a canonical resource.

The alternative was to encode Project IDs as JSON in the existing credential
row. That is simpler to migrate, but it makes uniqueness, atomic validation,
and per-Project authorization harder to enforce and conflates two credential
boundaries. A general Project membership or ACL table was rejected because it
would introduce a user/role model outside this change.

### 3. Enforce one security and admission pipeline

Every direct request follows this order:

1. Authenticate the Bearer PAT and resolve `ExternalAgentCaller`.
2. Check route scope and the caller's Project grant.
3. Check canonical resource ownership for resource-targeting routes.
4. Validate route values, headers, query parameters, and JSON syntax without
   creating domain state.
5. Normalize the accepted request and compute the versioned fingerprint.
6. Reconcile the durable request mapping.
7. Invoke canonical admission only for a new mapping, then wait for the
   required durable mapping/projection checkpoint.

Write handlers will read the request body as raw JSON only after steps 1 through
3. This avoids relying on automatic body binding before authorization and makes
duplicate-property detection and the strict `text`-only contract explicit.
The follow-up derives Project and Agent from the canonical Session; the stop
route derives its target revision and binding from the canonical Turn. All
timestamps and deadlines use `TimeProvider`.

The alternative was to rely solely on endpoint scope metadata and ordinary
model binding. That handles coarse scope checks, but it cannot enforce the
Project grant or resource ownership before idempotency/body admission, and it
would make invalid binding behavior observable on unauthorized resources.

### 4. Use a durable external request ledger before canonical effects

Add an `ExternalAgentRequestMapping` persistence model with a unique key made
from the credential identity, command kind, canonical resource scope, and
opaque `Idempotency-Key`. The row stores the contract version, fingerprint,
canonical IDs when known, an internal reconciliation identity, durable
acceptance/rejection/unknown state, and timestamps. It never stores the raw
request or returns the fingerprint.

The first request claims the row atomically before invoking a grain. A retry
with the same caller, scope, key, and fingerprint reads the row and reconciles
the existing canonical identity. A different fingerprint returns stable `409
idempotency_key_reused`. Definitive admission rejection is written as a
terminal decision in the same ledger. A crash after claiming the row is
recovered by retrying the same internal reconciliation identity; it cannot
mint another effect.

The external launch identity includes `callerKeyId` in addition to Project and
key before it reaches the existing launch coordinator. The coordinator remains
responsible for its prepare/ensure/submit fences and canonical Job/Session/
Input/Turn convergence, but the external ledger is the caller-isolated public
retry boundary. Follow-up passes a derived, caller-scoped internal key to the
Session admission path. Stop persists the target revision, context generation,
binding or explicit null binding, and deadline in the ledger before issuing
the control effect.

The alternative was to reuse the current launch coordinator key directly and
let the Session grain or stop operation own all idempotency. That would leave
launch keys shared across callers, would not cover durable rejection and
projection lag uniformly, and would expose the current stop operation identity
as the retry contract. An in-memory cache was rejected because it cannot
recover after process loss.

### 5. Build a separate public projection and public event journal

Add a projector fed by durable canonical AgentJob/AgentSession facts and their
outboxes. The projector writes, in one database transaction:

1. the allowlisted `PublicExecutionRead` snapshot;
2. the public Session event rows and their stable identity;
3. the source checkpoint/watermark, stream generation, and next sequence.

The projection has a Job-anchored record for a prepared launch, so a caller can
read `accepted` with a Job ID while Session acceptance is still pending. Once
the Session facts exist, the same anchor is joined to the canonical Session,
Input, and Turn IDs. Public reads never query a partially joined set of grains
or serialize the existing `AgentSessionEvents` payload.

The public event journal is separate from `AgentSessionEvents` even though the
latter is one of its inputs. The projector filters source transitions into the
finite public event vocabulary, applies the five-state precedence rules, and
maps output/error through explicit allowlists. The journal has per-Session
strictly increasing sequences, deduplication by normalized source transition,
retention metadata, and closed-stream tombstones.

The `after` cursor is an opaque, tamper-evident token containing the Project,
Session, stream generation, and exclusive sequence position. Its codec is
versioned and server-owned; handlers never accept a caller-decoded sequence.
Old-generation and malformed cursors fail with `cursor_invalid`, retained
prefixes fail with `cursor_expired`, and a source watermark ahead of the
projection returns `projection_lag` rather than stale data or `unknown`.

The alternative was to read grains directly on every request or expose the
existing CloudEvent rows with a cursor added at the HTTP layer. Direct reads
cannot provide a consistent cross-aggregate observation after response loss;
raw events leak internal data and do not have public sequence/generation
semantics. A dedicated projection adds storage and eventual consistency, but
it gives the API one privacy and ordering boundary.

### 6. Keep stop and terminal outcomes behind one fence

The public stop service will resolve exactly one canonical Turn, capture its
current revision and control context in the request mapping, and commit a
terminal fence before contacting the Runner. Repeated matching keys return the
stored public observation. A different key cannot supersede an unresolved stop
and receives `stop_outcome_unknown`.

The projector treats the terminal fence as the precedence boundary. The first
durable terminal fact wins; a late Runner result, delayed internal CloudEvent,
or projector replay can be recorded internally for diagnostics but cannot
rewrite public outcome, output, error, or sequence. This preserves the existing
Session/Runner control authority while giving the direct API a stable result.

The alternative was to make stop a best-effort Runner call and infer the result
from the next Session event. That would permit duplicate effects after a lost
response and could allow a late result to overwrite a stop outcome.

## Risks / Trade-offs

- [Projection lag] Canonical facts and public reads are not one cross-aggregate transaction. -> Persist source watermarks with snapshots/events, return `503 projection_lag` when a required watermark is ahead, and expose checkpoint age metrics.
- [Request-ledger crash window] A process can fail after claiming a key but before the canonical grain acknowledges it. -> Store a stable reconciliation identity before the effect, use existing grain recovery fences, and resume only from the original mapping.
- [Credential migration complexity] Existing PATs have no External Agent grant. -> Treat absent grants as deny, add only additive grant tables, and make grant creation explicit through the updated CLI/API.
- [Public-data leakage] Existing Agent and Runner DTOs contain prompts, paths, identities, and provider details. -> Use dedicated public records and allowlist serialization; add privacy tests that assert forbidden fields are absent.
- [Event storage growth] A retained public journal is additional durable data and cannot follow transcript compaction. -> Add configurable retention/pruning, retain cursor tombstones for the same recovery window, and monitor per-Session journal size.
- [Cursor key rotation] A signing-key change can invalidate active client cursors unexpectedly. -> Version the cursor codec and retain verification keys for at least the cursor retention window; finalize rotation policy before enabling the route.
- [Stop/result race] Stop, Runner result, and projector delivery can arrive in different orders. -> Make the terminal fence and revision part of the canonical stop decision and apply it again in the projection reducer.
- [Backfill ambiguity] Historical internal events do not all have the public vocabulary or safe final-output shape. -> Backfill only from durable canonical facts that satisfy the public mapper; mark unresolved observations as `unknown` or keep the route disabled for an unprojected Session rather than fabricating history.

## Migration Plan

1. Add additive database migrations and stores for External Agent grants,
   request mappings, public snapshots, public Session events, projection
   checkpoints/generations, and cursor tombstones. Add unique indexes for
   caller-scoped keys, event identity, and `(SessionId, sequence)`.
2. Ship the domain/application contracts and projector behind a disabled
   feature flag. Start projection workers in observe-only mode, verify source
   checkpoints and safe-field mapping, and backfill retained Sessions where
   canonical facts are sufficient.
3. Extend `mo auth token create`, `list`, and the server PAT routes with repeated
   `--project` and `--all-projects`. Validate all bindings before persistence;
   existing PATs remain valid on existing surfaces but have no direct API
   access until explicitly granted.
4. Add the `/api/v1` route group, direct PAT authentication branch, grant and
   resource authorization, request ledger, public read mapper, cursor reader,
   and stop fence adapter. Keep the existing `/api/projects` routes unchanged.
5. Run server integration/spec coverage for authorization order, all public
   states, response-loss retries, conflicting keys, rejection persistence,
   stop races, cursor expiry/generation, projection lag, retention, and privacy.
   Run CLI coverage for grant validation and token disclosure.
6. Enable the feature only after the projector has caught up for newly created
   and selected backfilled Sessions. Publish `docs/auth.md`,
   `docs/agent-api.md`, and the implementation-status guidance with the exact
   shipped contract.

Rollback is additive and feature-flag based. Disable new `/api/v1` admissions
and stop new grant issuance, but keep the request ledger and canonical work;
do not delete mappings or replay unresolved effects. The projector may be
paused and resumed from its checkpoint. Existing Web, CLI, Runner, Agent
Connection, and internal Session routes continue to operate. Database rollback
must not drop grant, mapping, or projection data while any deployed instance
can still read it; a later deployment can re-enable the feature and continue
from the durable checkpoints.

## Open Questions

- What retention duration and maximum journal size should apply to public
  Session events and cursor tombstones?
- Which existing AgentJob and AgentSession source facts are sufficient to
  reconstruct every public transition and safe final output for historical
  Sessions, and which new typed outbox facts are needed?
- Where should cursor verification keys live, and how long should old key
  versions remain valid during rotation?
- Should `operator_all` include only currently private Projects at request
  time, or also preserve access to a Project that later changes ownership or
  visibility?
- What policy should limit the size and content of persisted public final
  output and safe error messages?
- Should the first release expose backfilled historical Sessions, or enable
  event replay only for Sessions whose public projection was created after the
  migration?
