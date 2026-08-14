## Context

The target contracts for this change are already written and `wip`: `design/agent-api.md` (628 lines:
routes, idempotency, projection, cursors) and `design/auth.md` (`ExternalAgentCaller`, Project
grants). `docs/agent-api.md` carries `status: wip-not-implemented` and explicitly warns callers not
to build against it. What exists today:

- **Auth surface**: `AuthResolutionMiddleware` (`Auth/Identity`) authenticates every `/api` request
  with a first-hit carrier order — `Authorization: Bearer` then the `mohist_session` cookie — and
  resolves a `MohistPrincipal` with scopes. `Credential` (`Auth/Domain/Credential.cs`) has scopes,
  TTL, revocation, and a single nullable `ProjectId` used to constrain integration credentials.
  There is no Project grant model, no per-request caller key identity, and no route surface where a
  cookie cannot substitute for a token.
- **PAT issuance**: `mo auth token create` (`MohistCliCommands.Auth.cs`) posts
  `{name, scope, ttlHours}` to `POST /api/auth/tokens` (`AuthTokenRoutes` + `PatPolicy`). No grant
  options exist.
- **Canonical execution** (the composition surface this API must adapt, not duplicate):
  `IAgentLauncher.LaunchIdempotentAsync` funnels launch through
  `AgentLaunchCoordinatorGrain` keyed `(projectId, Idempotency-Key)`, which durably persists the
  launch plan, mints Job/Session/Input/Turn IDs, detects fingerprint conflicts
  (`LaunchIdempencyConflictException`), supports pre-minted IDs, and replays to the same
  identities. That key has **no agent dimension** — D4 therefore derives a scope-qualified
  coordinator key for direct launches instead of forwarding the caller's raw key. Follow-up goes
  through `AgentSessionGrain.BeginFollowupAsync` (reservation lease) and
  `AgentSessionFollowupDispatcher`. Stop has a canonical claim lifecycle:
  `ClaimTurnStopAsync(turnId, operationId)` → `MarkTurnStopDispatchedAsync` → `CompleteTurnStopAsync`
  with `StopOperationDeadline`, plus `StopQueuedTurnAsync` for queued turns and a terminal fence in
  `MarkTurnTerminal`. Admission/capacity gating is the #520 contract (archived, implemented);
  the complete fenced stop-operation lifecycle (#562) is still target state.
- **Durable outbox facts**: every aggregate commits CloudEvents rows — `AgentSessionEventRow`
  (`/mohist/agent-session/{id}`, per-source sequence) and `AgentJobEventRow` — alongside its state
  in SQLite (EF Core, single deployment). `EventPushQueue` offers in-process push notification on
  top of the durable store.

**Constraints**: single-administered deployment on SQLite (single relational store, one writer at
a time per transaction); no new external packages; the API must not create a second execution
lifecycle, queue, or event bus; Web UI, Runner, and Agent Connections keep their own adapters and
must be unaffected except for shared canonical facts.

## Goals / Non-Goals

**Goals:**

- Ship the seven-route `/api/v1` direct surface with Bearer-PAT-only authentication resolving an
  `ExternalAgentCaller` (callerKeyId, Principal, scopes, Project grant).
- PAT Project grants (`explicit` set / `operator_all`), persisted explicitly and granted at
  issuance via `mo auth token create --project ... | --all-projects` with atomic, all-or-nothing
  binding.
- Authorization strictly before lookup, idempotency, and admission: 403-before-404 for
  out-of-grant Projects; zero durable side effects on 401/403 (audit log only).
- Server-computed fingerprints and durable per-scope idempotency mappings for launch, follow-up,
  and stop — including durable admission rejections (200 `terminal/rejected`) and stable
  `409 idempotency_key_reused` conflicts.
- Keyed stop mapped to one canonical per-target fenced stop operation with a Server-frozen target,
  caller-bound mappings, and the `409 stop_outcome_unknown` supersession rule.
- One strict 22-field `PublicExecutionRead` allowlist with the five-state aggregate and fixed
  precedence, served exclusively from a durable, checkpointed public projection (snapshot + event
  journal + watermark in one transaction; `503 projection_lag` when behind).
- Resumable per-Session public events: opaque tamper-evident cursors, exclusive-after resume,
  strictly increasing per-Session sequences, stream generations, retention floor (`410
  cursor_expired`), closed-stream tombstones, documented dedup rules.
- Flip `docs/agent-api.md` and the `design/agent-api.md` / `design/auth.md` status sections to
  implemented.

**Non-Goals:**

- No Runner/Runtime/workspace/model/instructions/Skills selection routes; no generic Session,
  operation, transcript, or internal-event export routes.
- No second execution lifecycle, queue, or event bus — the API composes `IAgentLauncher`,
  `AgentSessionGrain`, `AgentJobGrain`, and the canonical stop lifecycle.
- No multi-user identity, cross-user visibility, RBAC, encryption at rest, OAuth, or general
  developer platform (auth.md non-goals).
- No changes to Web UI, Runner, or Agent Connection adapters beyond shared canonical facts.
- No external Session delete route; no time-based public event compaction in v1.
- No new external packages.

## Decisions

### D1 — New `ExternalAgent` module with a dedicated `/api/v1` route group and a Bearer-only auth sub-surface

New module directory `packages/server/src/Mohist.Server/ExternalAgent/` with sub-namespaces
`Routes`, `Identity`, `Idempotency`, `Projection`, `Cursors`. Routes register as one minimal-API
group under `/api/v1/projects/{projectId}/...` with exactly the seven specified endpoints (a route
enumeration test pins the surface).

Authentication reuses `AuthResolutionMiddleware` but the middleware gains a `BearerOnlySurface`
rule: paths under `/api/v1` skip the `mohist_session` cookie fallback entirely, so a cookie-only
request fails 401 with the existing uniform `WWW-Authenticate: Bearer` challenge. On this surface
the middleware resolution result additionally exposes the resolved Credential's stable ID
(`callerKeyId`); a new `ExternalAgentCallerResolver` endpoint filter then loads the Credential's
scopes and Project grant and produces the `ExternalAgentCaller` consumed by every subsequent
decision. Trusted Agent Connection identities are never consulted — they are different adapters.

**Alternatives considered**: (a) a `MapWhen` pipeline branch with its own middleware stack —
rejected, it duplicates the constant-time file-credential comparison, hash lookup, and
expiry/revocation logic that already live in one place; (b) letting the group's endpoint filters
re-resolve the Bearer token — rejected, double token resolution and two sources of truth for the
caller identity. Extending the existing middleware keeps one authentication path and makes
"cookie cannot substitute" a property of the surface, not of each route.

### D2 — Grant persistence: `ProjectGrantKind` column + `CredentialProjectGrant` child rows

`CredentialRow` gains `DirectApiProjectGrantKind` (null | `explicit` | `operator_all`); a new
`CredentialProjectGrants` table stores `(CredentialId, ProjectId)` pairs with a unique index.
Invariants, enforced at issuance and re-checked at resolution: `explicit` ⇒ non-empty set;
`operator_all` ⇒ empty set; absent kind ⇒ direct API denied (this is exactly the pre-existing-PAT
behavior the spec requires — no backfill needed); `operator_all` is *persisted*, never inferred
from an `operator` scope at resolution time (issuance enforces `--all-projects` requires operator
scope, but authorization reads only the persisted grant). The existing nullable `ProjectId` column
stays untouched — it means "integration credential binding", a different concern with different
semantics.

`ExternalAgentCaller` (record: `callerKeyId`, `principalId`, `scopes`, `projectGrant`,
`allowedProjectIds`) is resolved per request and never persisted beyond the request.

**Alternatives considered**: (a) a JSON column holding the grant — rejected: per-pair uniqueness
and issuance-time FK validation against Projects are exactly what the child table makes cheap and
transactional; (b) reusing `ProjectId` — cannot represent a set or `operator_all`; (c) a
standalone grants table not owned by the credential — rejected, the grant is a property of one
credential and must die with its revocation.

### D3 — Procedural authorization ordering: caller → scope → grant → membership → validation → fingerprint → mapping → admission

The ordering is the security contract, so it lives in one explicit pipeline inside the route
group, not in scattered attributes:

1. `ExternalAgentCallerResolver` filter: Bearer PAT → `ExternalAgentCaller`; failure 401 (no
   mapping read, no rejection tombstone).
2. Route scope check: writes require `operator`; reads accept `readonly` or `operator` (route
   metadata, mirroring `RouteScopeRequirement` conventions). Failure 403.
3. Project grant check against the path `projectId` *without resolving the Project*: `operator_all`
   passes; `explicit` must contain the ID. Failure 403 even when the Project does not exist — this
   is the 403-before-404 oracle prevention.
4. Resource membership: for Agent/Job/Session/Input/Turn routes, resolve the canonical record and
   check its `ProjectId`; absent or cross-Project → 404 with the resource code
   (`job_not_found`, `session_not_found`, ...). A follow-up resolves the Session *first* and
   derives Project and Agent from it — no body or query value can select them.
5. `Idempotency-Key` header validation (400 `idempotency_key_required`/`_invalid`) and strict JSON
   parsing (400 `invalid_request`) — before any domain state exists.
6. Fingerprint computation, then the atomic mapping lookup, then admission only on a miss.

On 401/403 the only durable artifact is an `AuthAuditEvent`; the pipeline above guarantees this by
construction because every store write happens after step 5.

**Alternatives considered**: declarative policy attributes per route — rejected because the
ordering between grant check, resource lookup, and idempotency lookup is observable behavior the
spec pins scenario-by-scenario; keeping it procedural in one file makes the order reviewable and
testable as a unit.

### D4 — Direct-owned `DirectAgentRequestMapping` table as the idempotency boundary of record; canonical engines below stay identity-idempotent

One new table, `DirectAgentRequestMappings`, unique on `(scopeKind, scopeId, idempotencyKey)`
where scope is `(projectId, agentId)` for launch, `(sessionId)` for follow-up, and
`(turnId)` plus bound `callerKeyId`/`projectId`/`sessionId` columns for stop (the stop-specific
caller binding the spec requires). Columns: fingerprint, status (`pending`/`finalized`), outcome
payload (canonical identity references — jobId/sessionId/inputId/turnId — or a durable rejection),
and for stop the frozen internal target snapshot (see D6).

Protocol (reserve → drive → finalize):

1. **Reserve** (one transaction): insert the mapping with fingerprint and *pre-minted* canonical
   IDs (launch: jobId/sessionId/inputId/turnId minted here; follow-up: inputId/turnId). Unique
   index conflict ⇒ compare fingerprints: equal ⇒ replay path; different ⇒ 409
   `idempotency_key_reused` (stable, no effect). `operator_all`/grant and scope checks have
   already passed at this point by D3.
2. **Drive**: execute the canonical pipeline with the pre-minted IDs — launch goes through
   `IAgentLauncher.LaunchIdempotentAsync` (the coordinator adopts pre-minted session/input/turn
   IDs verbatim today), follow-up through the Session follow-up path with the pre-minted pair, so
   a crash between reserve and finalize cannot mint a second execution on replay.
   **Coordinator-key derivation (launch):** the direct layer never passes the caller's raw
   `Idempotency-Key` into the launcher. The coordinator grain is keyed
   `(projectId, idempotencyKey)` with no agent dimension (`AgentLaunchCoordinatorCodec.KeyFor`),
   so a raw key would make the same key collide across Agents in one Project (surfacing as a
   spurious `LaunchIdempotencyConflictException` or a stuck `pending` mapping) and would share
   one key space with product-route launches that forward raw keys verbatim. Instead the direct
   layer derives a deterministic, scope-qualified coordinator key — SHA-256 over
   `"direct-launch-v1" || projectId || agentId || callerKey`, i.e. exactly the launch
   idempotency scope and nothing else (no fingerprint input, no `callerKeyId`: two callers
   sharing a key on one Agent also share the direct mapping, so they must share the grain) —
   tagged with a `\u001f`-delimited prefix (the codec's own unit-separator convention) and passed
   as the launcher's `idempotencyKey` argument, so grain key, persisted plan `IdempotencyKey`,
   and replays stay consistent. The delimiter is a control character that cannot appear in a
   direct key (the direct surface validates printable ASCII) and cannot be carried in an HTTP
   header value at all, so no caller-suppliable product key can equal a derived key — a
   cross-surface or cross-Agent grain-key collision is impossible, not merely improbable: same
   key on a different Agent ⇒ different grain ⇒ fresh execution, never a 409; a product launch
   using the identical key string in the same Project ⇒ different grain ⇒ no interference in
   either direction. The drive also passes the byte-exact prompt with
   `ExactPromptFingerprint = true` (the coordinator request already supports it) so the
   canonical fingerprint preserves text identity the same way the direct fingerprint does.
   **`LaunchIdempotencyConflictException` surfacing:** with D5's fingerprint gate at reserve and
   a coordinator key that is a pure function of scope + key, the same scope+key+fingerprint
   always rebuilds the same envelope on the same grain, so a coordinator conflict is unreachable
   in correct operation; if one ever surfaces it is treated as an internal invariant violation
   (500 `internal_error`), never translated into a caller-facing 409 — the direct mapping owns
   fingerprint semantics on this surface.
3. **Finalize** (one transaction): record the canonical outcome — accepted identities, or the
   durable admission rejection (capacity gate verdict) — in the mapping, then respond.

Replay of a finalized mapping returns the recorded identities together with their *current* public
observation (status/timestamps/output/error/sequence may advance). Replay of a `pending` mapping
re-drives step 2 (identity-idempotent) or returns the current observation /
`503 projection_lag` per D7. A durable admission rejection is stored as the mapping outcome and
replayed as 200 `status=terminal, outcome=rejected` forever — capacity recovery cannot resurrect
it. Rejections carry null live Input/Turn IDs because none were created.

**Alternatives considered**: (a) relying solely on the existing `AgentLaunchCoordinatorGrain`
fingerprint — rejected: it fingerprints the normalized product request (the launcher trims the
prompt), while the direct contract must distinguish texts differing only by whitespace or case;
the direct layer needs its own versioned fingerprint gate before delegation; (b) wrapping mapping
+ admission in one SQLite transaction — impossible: admission spans Orleans grains
(Job/Session) with their own persistence; the reserve/pre-mint protocol is what makes the split
safe; (c) persisting the raw body for later comparison — rejected by spec; only the fingerprint
is stored.

### D5 — Fingerprint: versioned canonical form, strict single parse, SHA-256

The body is parsed exactly once with a strict reader that rejects unknown properties and duplicate
JSON property names (v1 accepts only `text`, required, non-empty after validation). The
fingerprint input is `contractVersion || commandKind || canonicalScopeIds || canonicalBodyJson`
where `canonicalBodyJson` is deterministic canonical JSON (sorted property names) with the `text`
value preserved byte-exactly as parsed — no trim, no case folding. SHA-256 of the UTF-8 encoding.
Stop fingerprints the empty body. The hash is the only persisted artifact; it never appears in any
response, and the caller can never submit a hash the Server trusts.

**Alternatives considered**: hashing the raw request bytes — rejected, it makes semantically
identical requests (key order, insignificant whitespace) collide into false 409s; the Server-side
canonical form is what makes "identical payload retried" match while "different text" conflicts.

### D6 — Keyed stop: mapping row *is* the frozen target; canonical claim lifecycle executes it

First keyed stop (after D3 steps 1–5): the reserve transaction inserts the caller-bound mapping
and freezes the stop target — Turn revision, context generation, complete binding or explicit
null, deadline (`StopOperationDeadline` semantics) — as *internal* columns never exposed in any
response or event. The mapping also stores the Server-generated internal `operationId`. The route
then drives the existing canonical path: already-terminal Turn ⇒ durable no-op outcome, no Runner
call; queued Turn ⇒ `StopQueuedTurnAsync` local cancel, no Runtime contact; running Turn ⇒
`ClaimTurnStopAsync` + fenced stop delivery/complete with the frozen facts. A matching retry
resolves the mapping and returns the current Turn observation — it never re-reads the binding or
recomputes a deadline. A binding/context/owner change after the freeze cannot redirect the stop to
replacement work because the frozen target, not current state, drives the effect.

While the fenced outcome is `unknown`, a *different* key on the same Turn returns 409
`stop_outcome_unknown` (the caller reads the Turn); the same key recovers a lost response.
Completion-vs-stop races resolve through the existing terminal fence (`MarkTurnTerminal`): exactly
one terminal fact wins, at most one terminal public event, late observations cannot overwrite
outcome/output/error/sequence. Session admission stays blocked while unresolved, and no automatic
replay occurs — recovery is the existing stop-recovery reminder bounded by the frozen deadline.

Note: the complete one-way/deadline-bounded stop lifecycle is #562 (target state). This change
composes the current claim/complete contract and adopts #562's fence semantics when it lands;
the direct mapping layer is intentionally independent of that internal evolution.

**Alternatives considered**: accepting a caller-named operation ID — rejected by spec (the key is
the caller-visible identity; internal IDs are never caller-facing); issuing a fresh stop per key
when the previous is unknown — rejected, it would supersede unresolved effects and multiply
Runner-side interrupts.

### D7 — Public projection: four new tables, one projector, one transaction per transition

New tables under `Infrastructure/Data/ExternalAgent`:

- `PublicExecutionSnapshots` — one row per public target (`job|input|turn`, keyed by canonical ID,
  plus its `sessionId`): the allowlisted snapshot JSON, the internal terminal fence/revision, and
  the `observedSequence`. Job-anchored rows exist from the Job prepare fact onward, so a launch
  target is addressable before Session acceptance (null live IDs) and is updated in place after
  acceptance.
- `PublicSessionEvents` — the journal: `(sessionId, generation, sequence)` unique, `type`,
  `occurredAt`, payload (execution event ⇒ `PublicExecutionRead`; `session.context_reset` ⇒ the
  smaller session payload). Sequences come from the stream allocator, never reused or renumbered.
- `PublicStreamStates` — per Session: `currentGeneration`, `nextSequence`, `earliestSequence`
  floor (null in v1), `latestSequence`, `closedAt` (tombstone).
- `PublicProjectionCheckpoints` — which durable outbox facts are consumed (max consumed event row
  id per source table, plus per-stream position).

A single projector (hosted background service; `EventPushQueue` wakes it, a durable poll loop is
the source of truth so crash recovery is replay-from-checkpoint) consumes `AgentJobEventRow` and
`AgentSessionEventRow` plus canonical row snapshots, normalizes transitions, and commits in **one
EF transaction**: affected snapshots + journal entries + stream allocation + checkpoint. A crash
before commit leaves nothing; a crash after commit resumes past the checkpoint and cannot emit a
second sequence for the same normalized transition (source event identity is part of the
normalization key). Terminal snapshots store the terminal fence/revision internally; a stale or
delayed fact cannot un-terminal a target. Rebuilds (operator-triggered, future) write a new
generation and swap atomically, preserving the global sequence allocator; old-generation cursors
then 400.

Reads and command responses are served *only* from this projection. The write path records, at
finalize, the source event row IDs its effects produced; if the projection checkpoint has not
consumed them, the route returns `503 projection_lag` (never stale state, never new admission).
`unknown` is emitted only when required facts are consumed and inconclusive — lag is 503, not
unknown.

**Alternatives considered**: (a) computing `PublicExecutionRead` on the fly from canonical rows —
rejected: the spec requires snapshot/journal/checkpoint mutual consistency and an explicit lag
signal, and per-read joins over four aggregates re-derive precedence on every request; (b)
projecting inside Orleans grains — rejected: the projection must join Job + Session events and
commit journal + checkpoint in one relational transaction, which the grains' per-aggregate
persistence cannot provide; (c) sourcing events from the SignalR/event bus — explicitly forbidden
by spec; the bus only wakes the projector.

### D8 — Five-state aggregate as a pure function + a dedicated response DTO

`PublicStatusProjector.Evaluate(facts)` implements the fixed precedence exactly as specified:
fenced terminal wins → durable rejection is `terminal/rejected` (null live IDs allowed) →
unresolved acceptance/dispatch/binding/stop/outcome fact projects `unknown` (with
`admission=blocked` when a Session exists) → `outcome_pending` is `running` → retryable dispatch
block stays `queued` + `admission=blocked` → running beats queued beats accepted. Component facts
(`jobStatus`, `sessionActivity`, `admission`, `inputStatus`, `turnStatus`, `outcome`) stay visible.
Input/Turn reads anchor to the requested record — a terminal target stays terminal inside an
active Session. `unknown`/`outcome_pending` never authorize replay.

`PublicExecutionRead` is a dedicated 22-property DTO serialized by its own writer — never
`AgentJobLaunchRead`, `AgentSessionRead`, `SessionInputRead`, `TurnResultRead`, or
`SessionOperationRead`. Every key is always present; nulls only where the canonical fact does not
exist; `observedAt` always; `output` is `{ "text": ... }` or null; `error` is a safe
`{ code, message }`. Error envelopes for the direct API use `{ "error": { "code", "message" } }`
(plus the `cursor_expired` sequence bounds), separate from the product `ApiResponse` shape.
Exhaustive serializer tests assert the exact key set; internal facts (Runner IDs, bindings,
fences, prompts, memory, paths) have no code path into the DTO.

**Alternatives considered**: reusing product read DTOs with an ignore-list — rejected: an allowlist
DTO is the privacy boundary; deny-lists rot as product DTOs grow fields.

### D9 — Cursors: HMAC-signed opaque payloads from a server keyring; tombstones in stream state

A cursor is `base64url(canonicalJson({v, keyId, projectId, sessionId, generation, after}) ||
hmac)`. The HMAC key comes from the existing secret-store infrastructure with a key id; rotation
adds a new key and retains old keys for verification for the cursor-retention window, so rotation
never mass-invalidates live cursors. Decode failure, signature mismatch, or a payload bound to
another Project/Session/generation ⇒ 400 `cursor_invalid`, no fallback, no translation, no event
read. `nextCursor` is the last event's cursor; an empty page's cursor sits at
`highWaterSequence`. Limit default/max 100. Resume is exclusively after.

Session deletion (control-plane only): the projector sets `closedAt` and keeps the minimal
tombstone row (`latestSequence`, `earliestSequence=null`) for the retention window — a valid
current-generation cursor gets 410 `cursor_expired` with the bounds; a cursorless read gets 404
`session_not_found`; after physical purge, 400 `cursor_invalid`. v1 retains all events while the
Session lives (transcript compaction must not touch the journal).

**Alternatives considered**: (a) plain sequential cursor (`?after=42`) — rejected: not
tamper-evident, not generation-bound; (b) encrypted cursor — rejected: HMAC gives the required
tamper evidence without key-dependent decode failures and without pretending the content is
secret (it contains only IDs the caller already knows — documented as opaque data regardless).

### D10 — Grant-aware issuance: atomic validation inside the existing create route

`POST /api/auth/tokens` accepts optional `projectIds[]` and `allProjects` alongside
name/scope/ttl. Validation order: issuer authentication (existing) → mutual exclusion of the two
grant forms → `allProjects` requires `operator` scope → every listed ID resolves as a private
Project of the deployment (`ProjectRefResolver`) → one transaction inserts Credential + grant
rows. Any failure ⇒ 403 and nothing persisted (no partial credential or grant row). The CLI adds
`--project` (repeatable) and `--all-projects` with client-side mirrors of the same rules for fast
feedback; the full token is still shown exactly once and only its hash stored. PATs created
without a grant remain fully usable on existing control-plane surfaces.

**Alternatives considered**: a separate "attach grant" endpoint after creation — rejected: the
spec requires atomic binding-failure semantics and forbids a window where an ungranted credential
exists half-bound.

### D11 — Docs flip last

`docs/agent-api.md` (`wip-not-implemented` → implemented), `docs/auth.md` grant options, the
`design/agent-api.md` / `design/auth.md` Status sections, and CLI help all flip in the final task,
after the routes, issuance, and projection are demonstrably wired — the docs are the shipping
signal, not the starting artifact.

## Risks / Trade-offs

- [SQLite single-writer contention: projector transactions, mapping reservations, and canonical
  event writes all serialize on one database] -> keep each projection transaction small and
  batched per wake-up; the projector is a single background writer with per-stream ordering, so
  it never competes with itself; mapping reserve/finalize transactions are two short inserts.
  Measure under load; the deployment model (single admin, self-hosted) bounds concurrency.
- [Crash between mapping reservation and canonical finalize leaves a `pending` mapping] ->
  pre-minted IDs (D4) mean replay re-drives an identity-idempotent pipeline: the launch
  coordinator adopts the same IDs, the Session grain's status-based idempotency collapses
  re-submission; until finalize the caller gets the current observation or 503 `projection_lag`,
  never a second execution.
- [Projection lag → 503 bursts right after writes] -> the projector is woken synchronously via
  `EventPushQueue` and lag is scoped to the just-written stream; retry semantics are safe (same
  key, same read); lag is a reconciliation condition, never `unknown`.
- [First-deploy backfill: the projector starts at checkpoint zero and must project existing
  history] -> bounded, one-time, batched replay from durable event tables; throttle the replay
  loop to avoid starving request-path writes; routes simply 503/serve-once-caught-up for legacy
  targets.
- [HMAC key rotation invalidates cursors] -> key id embedded in the cursor; old verify keys
  retained for the cursor-retention window; rotation is additive.
- [`operator_all` blast radius] -> persisted explicitly, audited at issuance, revocable
  immediately; docs steer callers to `explicit` grants; scope does not silently imply the grant.
- [Allowlist regression leaks internal facts] -> dedicated DTO + exhaustive key-set tests + a
  route-enumeration test (exactly seven routes); no code path from internal read models into the
  public serializer.
- [Stop unresolved forever: caller cannot supersede, admission stays blocked] -> the frozen
  deadline bounds the fence; the existing stop-recovery reminder resolves it; the cost is the
  spec's chosen safety trade (no supersession of unknown effects), surfaced as a documented
  `reasonCode`.
- [Dual idempotency surfaces (product `Idempotency-Key` routes vs direct API) drift] -> the
  direct mapping table is direct-only; launch delegation funnels through the same coordinator
  *engine* — pre-minted-ID adoption and replay-to-same-identities stay shared — while each
  surface keeps its own key space: direct launches address the coordinator through the D4
  derived, scope-qualified key, so the raw caller key never reaches the canonical layer and the
  two surfaces can neither collide nor replay each other; documented in the module.

## Migration Plan

1. **Schema (additive, inert)**: one EF Core migration adds `DirectApiProjectGrantKind` +
   `CredentialProjectGrants`, `DirectAgentRequestMappings`, `PublicExecutionSnapshots`,
   `PublicSessionEvents`, `PublicStreamStates`, `PublicProjectionCheckpoints`. No existing table
   is altered semantically; older binaries ignore the new tables.
2. **Server release**: `/api/v1` group + auth sub-surface + caller/grant resolution + issuance
   validation + mapping + projector (starts backfill from checkpoint zero — bounded replay of
   existing durable events). Nothing is reachable until a PAT carries a grant, so the feature is
   dark until an operator opts in with `mo auth token create --project ...`.
3. **CLI release**: `--project` / `--all-projects` options.
4. **Docs flip** (D11) once routes and issuance ship.
5. **Rollback**: revert the server binary — routes disappear; granted PATs remain valid
   control-plane credentials; the additive schema is harmless to leave in place (and safe to keep
   for a re-rollout); no destructive migration exists. Rolling back the CLI alone leaves grant
   issuance available through the HTTP API.

Verification per step: route-enumeration and allowlist tests; auth-ordering tests (403-before-404,
zero-side-effect assertions against every store); idempotency replay/conflict/rejection tests;
projector crash-recovery (kill between transactions, assert no partial snapshot/sequence) and
lag tests; cursor tamper/generation/tombstone tests.

## Open Questions

- **Projector hosting**: hosted background service (chosen for D7's single relational transaction)
  vs an Orleans projection grain (free per-Session serialization). Confirm the service's
  per-stream ordering is sufficient under concurrent wake-ups, or add a per-stream queue.
- **Watermark granularity**: per-source-table global max event row id vs per-stream positions —
  decide based on how cheaply the write path can record "required watermark" at finalize (D7).
- **Follow-up engine reuse**: extend `AgentSessionFollowupReservation` to adopt pre-minted
  Input/Turn IDs (mirroring the launch coordinator), or add a thin follow-up coordinator so
  reserve/replay is symmetric with launch. Recommendation: extend the reservation.
- **`session.unknown` emission policy**: how aggressively to emit (per affected target vs deduped
  per Session) without journal spam while preserving the "at least one component fact unknown"
  guarantee.
- **Backfill bound**: batch size/throttle for large existing deployments; whether a
  startup-progress metric is needed on first deploy.
- **Cursor-retention window default** for closed-stream tombstones and HMAC old-key retention
  (e.g., 30 days) and where the purge job lives.
- **Rate limiting** on the direct surface: reuse `FixedWindowRateLimiter`? Not in spec — decide
  whether v1 ships without it.
