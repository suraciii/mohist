# Design: External Agent API — Authentication, Idempotency, and Resumable Reads

Reference: [proposal](proposal.md) for motivation and scope; the five capability
specs under [specs](specs) for the binding requirements. The target contract is
`design/agent-api.md` (currently `wip`); this document decides how the Server
implements it.

## Context

The previous slice (`issue-555-pat-project-grants`) shipped PAT issuance with a
persisted `DirectApiProjectGrant` (`explicit` / `operator_all`) on
`Credential`, validated at issuance. That grant is inert: no route consumes it.

What already exists and is reused rather than rebuilt:

- **Auth pipeline.** `AuthResolutionMiddleware` resolves a Bearer PAT (hash
  lookup via `ICredentialStore`) or the `mohist_session` cookie into a
  `MohistPrincipal`, enforces `RouteScopeRequirement` metadata, and rejects
  query-string tokens. It resolves the full `Credential` (including the grant)
  but discards everything the principal does not carry.
- **Canonical execution lifecycle.** `IAgentLauncher` /
  `AgentLaunchCoordinatorGrain` (durable idempotent manual launch keyed by
  `(projectId, idempotencyKey)` with a request fingerprint and pre-minted
  Job/Session/Input/Turn IDs), `AgentSessionGrain` follow-up input, and the
  fenced stop lifecycle (`ClaimTurnStopAsync`, `StopQueuedTurnAsync`,
  `ISessionStopDelivery`, `ApplyStopDeliveryAsync`).
- **Durable event journals.** `AgentJobEventRow` and `AgentSessionEventRow`
  persist one CloudEvents envelope per canonical lifecycle fact via `EventStore`
  (SQLite, EF Core, append-only with per-source sequences).
- **Docs target.** `design/agent-api.md` fully specifies the public contract:
  route table, `PublicExecutionRead` allowlist, five-state precedence, error
  envelope, cursor rules, stop fencing. The design below implements it; it does
  not renegotiate it.

Constraints:

- SQLite is the only database; projection transactions must fit its
  single-writer model.
- No new execution lifecycle, queue, Runtime Session, or client-owned event log.
- Zero breaking changes: existing control-plane routes, Web UI, CLI, Runner, and
  Agent Connections are untouched; PATs without a grant keep working elsewhere.
- 401/403 paths have zero side effects, including no idempotency reads.
- The Web launch route's coordinator scope `(projectId, key)` is an existing
  public behavior and must not change.

## Goals / Non-Goals

**Goals:**

- Ship the `/api/v1` direct boundary: 3 writes and 4 reads exactly as specified
  in `design/agent-api.md`.
- Resolve every request to an `ExternalAgentCaller` from the persisted PAT
  grant and enforce Bearer-only + scope + Project grant before any resource
  lookup, validation, idempotency, or admission.
- Server-owned durable idempotency for launch/follow-up/stop with
  server-computed fingerprints, replay, `409 idempotency_key_reused`, durable
  admission rejections, and `409 stop_outcome_unknown`.
- Durable public projection (`PublicExecutionRead` snapshots + public event
  journal + source checkpoint in one transaction), crash recovery, terminal
  fences, stream generations, and `503 projection_lag`.
- Opaque, tamper-evident, generation-bound cursors with `400 cursor_invalid` /
  `410 cursor_expired` semantics and closed-stream tombstones.
- Move `design/agent-api.md` and `design/auth.md` to shipped; update
  `docs/agent-api.md`, `docs/auth.md`, and the README implementation table.

**Non-Goals:**

- Any change to the existing Web/CLI launch, follow-up, stop, or session routes
  (including their idempotency contracts).
- New execution states, queues, admission logic, or Runtime/Runner surfaces.
- Cross-user visibility, OAuth clients, RBAC, encryption at rest.
- Compaction, time-based retention, or Project-wide streams for public events.
- An internal-operation lookup route for stop; response-loss recovery is replay
  of the same keyed POST.

## Decisions

### A. Boundary placement: dedicated middleware + route group

A new `ExternalAgentApiMiddleware` runs immediately after
`AuthResolutionMiddleware` for paths under `/api/v1`. It does not re-resolve
the token; instead `AuthResolutionMiddleware` is extended minimally to record
two facts it already knows: the credential carrier kind (bearer vs cookie) and
the resolved `ExternalAgentCaller` (built from the `Credential` it already
loaded — `callerKeyId = Credential.Id`, principal, scopes,
`DirectApiProjectGrant`). Both land in `HttpContext.Items`.

The new middleware then enforces, in order, before any endpoint runs:

1. Carrier must be `bearer`. A cookie-resolved principal → `401` with
   `WWW-Authenticate: Bearer` (same non-classifying body as the auth layer).
2. `ExternalAgentCaller` must exist (PAT grant non-null and non-empty) → else
   `403 forbidden`.
3. Route scope (`operator` for writes, `operator|readonly` for reads) via the
   existing `RouteScopeRequirement` metadata on the group → else `403`.
4. The route's `projectId` value must pass the grant (`explicit` list or
   `operator_all`) → else `403`, regardless of whether the Project exists.

Resource Project-membership checks (a foreign Job is `404 job_not_found`, not
403) stay in the endpoints, after the grant passes. Because steps 1–4 run
before any endpoint delegate, 401/403 paths structurally cannot touch
idempotency, admission, or effects.

Routes live in a new `Api/DirectApi/` folder registered as one
`/api/v1/projects/{projectId}/...` map group, deliberately outside the
control-plane route files (whose product read shapes are the wrong contract).
Alternatives considered: doing everything in `AuthResolutionMiddleware`
(rejected — it would entangle the direct boundary with every other surface and
make the PAT-only rule easy to regress); per-endpoint filters (rejected — the
grant-before-lookup ordering must be guaranteed once, centrally, and tested as
pipeline behavior).

### B. Idempotency mappings: one EF table, three command scopes

New table `direct_api_idempotency_mappings`:

| Column | Purpose |
|---|---|
| `command` | `launch` / `followup` / `stop` |
| `scope_key` | Canonical scope: `projectId\|agentId\|key` (launch), `sessionId\|key` (follow-up), `turnId\|callerKeyId\|key` (stop — caller-bound, see below) |
| `caller_key_id` | Credential ID; embedded in the stop `scope_key` (stop uniqueness is caller-scoped), attribution for the others |
| `fingerprint` | Server-computed SHA-256 of the versioned canonical request |
| `state` | `pending` / `completed` / `rejected` |
| `outcome` | Canonical IDs (`jobId`, `sessionId`, `inputId`, `turnId`), public rejection error code, or stop outcome |
| `frozen_target` (stop) | Frozen turn revision, context generation, binding, deadline, operation ID |
| timestamps | created / completed |

Unique index on `(command, scope_key)`. Because the stop `scope_key` embeds
`callerKeyId`, stop uniqueness is caller-scoped: caller B presenting caller
A's `(turnId, key)` never lands in A's replay path — B is always answered
from B's own request (B's own mapping, or the cross-caller block below),
never from A's row or outcome. A stop mapping additionally has a filtered
unique index on `turnId WHERE command='stop' AND state IN ('pending')` —
this is the database-level lock that makes any other stop request for the
Turn — a different key, or another caller replaying the first caller's key
— hit `409 stop_outcome_unknown` while the first stop's outcome is unknown.
SQLite supports partial indexes; EF Core expresses this via `HasFilter`.

Write path, identical shape for all three commands:

1. Validate key form (`1..128` printable ASCII) and body strictly (Decision C).
2. Compute the fingerprint: SHA-256 over a versioned canonical JSON object the
   Server builds itself — `{ v, command, projectId, agentId?, sessionId?, turnId?, body }`
   — with property order fixed by construction, text preserved byte-exactly
   (no trim, no case folding). Reuse the `AgentLaunchCoordinatorCodec.StableToken`
   hashing convention. The caller is deliberately not part of the fingerprint:
   stop caller separation comes from the caller-scoped `scope_key`, so two
   callers' identical stop requests legitimately carry the same fingerprint in
   their own rows.
3. `INSERT` the mapping row (`state=pending`) under the unique index. On
   conflict: load the existing row — same fingerprint → replay path; different
   fingerprint → `409 idempotency_key_reused` (stable, no effects). For stop, the
   insert also contends on the per-turn filtered index, producing
   `stop_outcome_unknown` while any other stop mapping for the Turn is still
   pending. The violated constraint is discriminated by index name: a
   `(command, scope_key)` hit is always the caller's own scope (replay or
   reuse conflict); a per-turn filtered-index hit is the cross-key block and
   persists nothing.
4. Perform the canonical operation once, then update the row to
   `completed`/`rejected` with the canonical IDs or public rejection.
5. Response = the mapped observation (see Decision E).

Cross-caller stop outcomes fall out of the two indexes. While caller A's
stop for a Turn is unresolved (`state=pending`), caller B — replaying A's
key string or using a different key — contends on the per-turn filtered
index and receives `409 stop_outcome_unknown`; nothing of B's is persisted.
Once no unresolved stop remains for the Turn, B's key inserts B's own
durable mapping (never A's row), and classification evaluates it against
the Turn's current state: already-terminal → durable no-op observation;
queued → local cancel; running → a new fenced stop operation. B is thereby
never served A's mapping, outcome, or frozen target.

The mapping is durable before the 200 returns. A crash between insert and
update leaves `state=pending`; a retry finds the row, and the canonical
operation is re-entered through its own idempotent grain (launch) or completed
by the pending-stop resolution (stop), so at most one execution exists.

Alternatives considered: extending `AgentLaunchCoordinatorGrain` to the direct
scopes (rejected — its `(projectId, key)` grain identity is a shipped public
behavior for the Web surface, and adding agent-scoped keys into the same
identity space risks collisions with the subagent 3-part keys); storing
mappings in Orleans state (rejected — the read/replay path and the projector
need cheap relational lookup by scope and turn, and EF ownership keeps one
transactional home with the projection tables).

### C. Strict request parsing and validation

Writes read the raw body once and parse with a small strict reader:
`JsonDocument` with `AllowTrailingCommas=false`, reject any property other
than `text`, reject duplicate property names (manual enumeration —
System.Text.Json accepts duplicates by default), require exactly one
non-empty `text` string. `text` is empty-string-invalid only; whitespace is
significant and preserved (`Fix the bug` ≠ `Fix the bug `). Launch/follow-up
bodies carry nothing else — no attachments, context, or options, so nothing
can be silently accepted or ignored. Stop requires an empty body. The
`Idempotency-Key` is read only from the header. All `400 invalid_request` /
`idempotency_key_required` / `idempotency_key_invalid` failures happen before
any mapping row exists.

Follow-up Project and Agent are always derived from the canonical Session
resolved by `sessionId`; the fingerprint contains only canonical route IDs and
the accepted body, so a caller cannot smuggle derived values.

### D. Command composition: adapt existing owners

- **Launch** resolves the Agent in the authorized Project (`404
  agent_not_found` for missing/archived), then calls
  `IAgentLauncher.LaunchIdempotentAsync` with a *derived internal* coordinator
  key — `StableToken(projectId|agentId|publicKey)` — so the coordinator's
  crash-safe prepare/ensure/submit dedup protects the direct launch without
  changing the Web surface's key identity. The public mapping row stores the
  derived key; a retry after a crash re-derives it deterministically and
  reaches the same coordinator grain and the same canonical
  Job/Session/Input/Turn group.
- **Follow-up** resolves the Session (membership → `404 session_not_found`),
  derives Project/Agent, and submits the Input through the session grain,
  pre-minting `inputId`/`turnId` deterministically from
  `StableToken(sessionId|key|followup-input)` so at most one Input/Turn pair is
  created per mapping.
- **Stop** resolves the Turn, classifies it, and reuses
  `AgentSessionStopOperations` unchanged: already-terminal → durable no-op
  observation, no Runner call; queued → local cancel (launch turn → job
  cancel); running → claim with `expectedOperationId` = the operation ID frozen
  in the mapping row, dispatch via `ISessionStopDelivery`, apply. The mapping
  row's `frozen_target` is written before the first Runner effect; a matching
  retry reads the frozen row and never re-reads the current binding. An
  `unknown` delivery outcome leaves the row `pending`, which is exactly what
  blocks any other stop request for the Turn — another key or another caller
  (`409 stop_outcome_unknown`) — until the fenced lifecycle resolves it.

Admission rejections (queue full, spawn admission denied) are definitive
outcomes of the canonical operation: the row moves to `state=rejected` with a
safe public error code, and the response is `200 status=terminal
outcome=rejected` — never a 5xx — so response-loss replay returns the same
durable decision.

### E. Public projection: three table families, one transaction

New tables under `Infrastructure/Data/PublicApi/`:

1. `public_execution_snapshots` — one row per public anchor
   (`anchor_type` = job / session / input / turn, `anchor_id`): the serialized
   `PublicExecutionRead` JSON (all 22 keys, nulls explicit), the internal
   terminal fence/revision, the Session binding, and the last projected
   sequence. Job anchors exist before Session acceptance (`jobStatus=
   preparing`, null live IDs) and are updated in place when the joined
   Job/Session/Input/Turn mapping projects.
2. `public_session_events` — one row per public event:
   `(session_id, generation, sequence)` unique, ascending, plus `type`,
   `occurred_at`, and the payload (`execution` snapshot or the smaller
   `session` payload for `session.context_reset`). The internal
   source-transition identity that produced it is stored for replay
   deduplication.
3. `public_stream_states` — per Session: `active_generation`,
   `next_sequence` (the global allocator, independent of generation),
   `earliest_sequence` floor, `latest_sequence`, `closed` tombstone flag,
   and the per-source-feed checkpoints (`agent_jobs`, `agent_sessions`, plus
   session tree/context feeds) proving which durable facts are consumed.

A `PublicExecutionProjector` hosted background service is the only writer. It
polls the canonical journals (`AgentJobEventRow`, `AgentSessionEventRow`) and
aggregate tables past each checkpoint, groups input per affected
Session/target, and commits snapshot upserts + journal appends + sequence
allocation + checkpoint advance + generation bookkeeping in **one EF
transaction per batch**. Write paths nudge it via an in-process channel for
latency; correctness never depends on the nudge (checkpoint-driven). SQLite's
single-writer model is fine: one projector process, short transactions.

Rules implemented inside the projector:

- **Terminal fence:** a snapshot's terminal state/revision is stored; later
  facts pass the fence or are dropped. Execution completion and stop race
  through the same fence; exactly one terminal public event is emitted.
- **Five-state precedence** exactly as specified (fence → rejection →
  unknown → `outcome_pending`=running+blocked → queued+blocked →
  running > queued > accepted), computed only from consumed facts.
- **Generations:** first commit creates generation 1. Ordinary restart/replay
  never changes it. A rebuild (operator-triggered, rare) builds a new
  generation's journal from canonical inputs, then flips
  `active_generation` atomically while `next_sequence` stays on the stream
  state — sequences are never reused or renumbered.
- **`session.context_reset`** is emitted only from a durable canonical
  ContextBoundary/reset fact, with the smaller session payload.
- **Crash recovery:** anything before commit leaves nothing partial; after
  commit the checkpoint skips already-projected source transitions (journal
  rows carry the source identity). No Runner, launch, follow-up, or stop
  effect is ever replayed by the projector.

`PublicExecutionRead`/`PublicEventPage` responses are **served only** from
these tables. A dedicated serialization DTO (`Api/DirectApi/PublicExecutionReadDto`)
with required properties makes the allowlist explicit; internal read shapes
(`AgentJobLaunchRead`, `SessionOperationRead`, transcripts) are never
serialized into it, and a production-contract architecture test asserts no
internal read type is referenced from `Api/DirectApi`.

### F. Read freshness: `503 projection_lag` via checkpoint comparison

Reads (Job/Input/Turn, events, and command-response replays) compare the
relevant source head (cheap `MAX(id)` on the canonical journals for that
Job/Session) against the stored checkpoint in the same request. Head ahead of
checkpoint → `503 projection_lag`, no effects, no stale body. This is a
transport condition and is never surfaced as the five-state `unknown`
(`unknown` is emitted only when consumed facts themselves are unresolved).
Alternative considered: snapshot-only reads without lag detection (rejected —
the spec explicitly forbids returning stale state as current after a write the
Server already accepted).

### G. Cursors: HMAC-bound opaque tokens

A cursor is `base64url(payload || HMAC-SHA256(payload))` where payload =
`{ projectId, sessionId, generation, afterPosition, version }`. The HMAC key
is a deployment secret persisted at first start alongside other server secrets.
Properties fall out naturally:

- Malformed/tampered/cross-Session/cross-Project/wrong-generation → signature
  or binding mismatch → `400 cursor_invalid`, no event read attempted, no
  translation of old-generation cursors.
- Expiry check happens only after validation: a valid current-generation cursor
  whose `afterPosition` < `earliest_sequence` floor → `410 cursor_expired`
  with safe `earliestSequence`/`latestSequence` (the tombstone case:
  `earliestSequence=null`, last safe `latestSequence`). No cursor at all
  against a deleted Session → `404 session_not_found`. After physical purge
  removes the tombstone, the cursor can no longer be recognized → `400
  cursor_invalid`.
- Empty pages position `nextCursor` at `highWaterSequence`.

Alternative considered: random opaque cursors stored server-side (rejected —
one row per issued cursor per page, unbounded growth; the HMAC token is
stateless, verifiable after restart, and rotation simply invalidates all
cursors, which clients are already required to handle as `cursor_invalid`).

Key rotation is therefore a safe operational lever, not a data migration.

### H. Error envelope and docs

`/api/v1` uses the spec's envelope `{ error: { code, message } }` (plus the
`410` sequence bounds), not the control-plane `success` envelope — the two
surfaces are intentionally distinct contracts. 401 carries
`WWW-Authenticate: Bearer`. Reason codes are drawn from a fixed public map
(`queue_full`, `context_reset`, `stop_outcome_unknown`, …) maintained in one
place with the internal-cause → public-code translation, so provider/stack
detail cannot leak.

Docs updates: `design/agent-api.md` and `design/auth.md` frontmatter to
shipped with status sections rewritten; `docs/agent-api.md` implementation-gap
section replaced by usage documentation; README implementation table updated.

## Risks / Trade-offs

- [Projector lag under bursts delays reads as 503s] -> Nudge channel + short
  poll interval; 503 carries `Retry-After`; the caller contract already makes
  retry the recovery path and forbids inferring state.
- [SQLite write contention between projector and canonical writes] -> One
  projector writer, small batches, busy-retry policy; projection is additive
  load, and canonical paths already tolerate the single-writer model.
- [Bearer-only middleware ordering regresses under future route additions] ->
  The `/api/v1` group is registered in one place with the middleware pinned to
  the path prefix; pipeline-order specs (cookie → 401, grant-less PAT → 403,
  out-of-grant project → 403 not 404) are pinned in SpecTests so a regression
  fails CI.
- [Derived coordinator key collides or drifts from the Web surface] -> Derived
  keys live in a distinct token namespace (`StableToken` of a direct-scope
  string) and are stored on the mapping row; the Web route's key identity is
  untouched.
- [Fingerprint versioning: contract changes make old replays conflict] -> The
  canonical object carries `v`; bumping it only on genuine contract changes
  means existing mappings keep replaying under the version they were created
  with (the stored fingerprint is compared, not recomputed with a new `v`).
- [HMAC key loss invalidates every cursor] -> Clients must already treat
  `cursor_invalid` as recoverable (reload observations, take a new cursor); key
  is persisted with the same durability as other server secrets, and loss is a
  deliberate, documented degrade.
- [Unresolved stop pins a Turn forever if the fence never resolves] -> This is
  the existing canonical stop-recovery lifecycle's problem to resolve; the
  mapping row surfaces its state and never invents a replacement effect, per
  spec.
- [Snapshot JSON drift (allowlist grows by accident)] -> The DTO has required
  members and is round-trip-tested for exact key sets in SpecTests; unknown
  keys fail the contract specs.
- [Durable rejections can never be retried into acceptance] -> Intentional per
  spec; the error body and docs say to use a new key for a genuinely new
  intent.

## Migration Plan

1. Additive EF migration (new tables only: idempotency mappings, snapshots,
   journal, stream states). No existing table changes; deployed servers apply
   it on start as usual.
2. Ship middleware + routes + projector in one release. The boundary is
   naturally gated: only PATs already carrying a persisted Project grant can
   authenticate, and grant issuance shipped earlier — those PATs go live on
   this deploy, which is the point of the change.
3. Docs move from target-spec to shipped in the same change.
4. Rollback: redeploy the previous build. The new tables are inert without the
   routes; no canonical data was transformed. Grant-bearing PATs lose direct
   access until the fix returns, which restores the pre-change behavior
   exactly.

Testing: SpecTests suites for each capability (auth ordering, idempotency
replay/conflict/durable-rejection, projection consistency/recovery/generations
including crash-before/after-commit simulation, read anchoring and allowlist
shape, cursor validity/expiry/tombstone/limit), plus one architecture rule
keeping internal read shapes out of `Api/DirectApi`.

## Open Questions

- Whitespace-only `text` (e.g. `" "`): currently accepted because text is
  byte-significant and the spec only rejects *empty* strings. Confirm callers
  are happy, or tighten to reject whitespace-only as `invalid_request` in v1
  before external users depend on it.
- Nudge fan-out: is the in-process channel sufficient for single-node SQLite
  deployments, or should the projector also observe a lightweight timer sweep
  during idle periods to bound worst-case lag? (Current plan: both — nudge for
  latency, timer as safety net; tune the interval from load tests.)
- Whether `docs/agent-api.md` gains curl-level examples now or in a follow-up
  docs pass (proposal only requires the gap sections to be updated).
- The operator-triggered projection rebuild entrypoint (control-plane, not
  `/api/v1`) — this change implements generation switching, but the exact
  admin command surface can land separately if scope pressure demands.
