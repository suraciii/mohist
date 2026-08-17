# Issue 555: External Agent API — Authentication, Idempotency, and Resumable Reads

## Why

External Agents and automation must delegate work to a Mohist Agent headlessly,
without the Web UI or a Runner credential. Today they have no direct API: the
existing launch, follow-up, and stop routes are internal control-plane surfaces
that return full product read shapes, and a caller that loses a response cannot
learn whether Mohist accepted its request without risking duplicate work. The
PAT Project grant delivered in the previous slice is still inert because no
direct route consumes it.

This change ships the versioned direct API boundary specified in
`design/agent-api.md`: Bearer PAT authentication with Project grants,
server-owned idempotency for writes, and a durable public projection with
cursor-based event reads that survives disconnects.

## What Changes

- Add the direct External Agent API under `/api/v1`: launch, follow-up, and
  stop writes plus Job, Input, Turn, and per-Session event reads, all keyed by
  canonical Mohist IDs and accepting only a minimal text body.
- Resolve every direct request's Bearer PAT to an `ExternalAgentCaller`
  (callerKeyId, principal, scopes, project grant) and enforce scope, Project
  grant, and resource Project membership **before** resource lookup,
  idempotency, and admission. 401/403 paths have zero side effects; cookies
  and trusted Agent Connection identities cannot substitute for the PAT.
- Require an `Idempotency-Key` on every write. The Server normalizes the
  accepted request and computes the fingerprint itself; a durable keyed mapping
  scopes launch, follow-up, and stop so a replay returns the original canonical
  Job/Input/Turn mapping and its current public observation, while key reuse
  with a different payload returns `409 idempotency_key_reused`. Definitive
  admission rejections are durable under the same key.
- Add a durable public execution projection: a strict `PublicExecutionRead`
  allowlist with the five-state aggregate (`accepted`, `queued`, `running`,
  `terminal`, `unknown`) and component facts, updated in one transaction with
  the public event journal and source checkpoint. Reads ahead of the
  checkpoint return `503 projection_lag` rather than stale state.
- Add a persisted public Session event stream with opaque, tamper-evident,
  exclusive-after cursors; strictly increasing per-Session sequences; stream
  generations for rebuilds; and `400 cursor_invalid` / `410 cursor_expired`
  (with safe sequence bounds) so a caller resumes exactly where it stopped.
- Route stop through the existing canonical fenced stop lifecycle, binding the
  caller-visible stop key to the frozen target; an unresolved stop blocks
  supersession with `409 stop_outcome_unknown` and never replays the effect.
- No new execution lifecycle: the API adapts existing AgentJob, AgentSession,
  SessionInput, AgentTurn, admission, and recovery owners. It exposes no
  Runner, Runtime, workspace, transcript, or prompt content.

## Capabilities

- `external-agent-caller-auth`: Bearer-PAT-only direct boundary, runtime
  `ExternalAgentCaller` resolution from the persisted credential grant, scope
  and private-Project grant authorization, and the zero-effect security
  ordering before validation, idempotency, and admission.
- `external-write-idempotency`: required Idempotency-Key, server-computed
  normalized request fingerprints, durable keyed mappings and their scopes for
  launch, follow-up, and stop, replay and durable-rejection semantics, and the
  `idempotency_key_reused` / `stop_outcome_unknown` conflict rules.
- `public-execution-projection`: the checkpointed durable projection — snapshot
  allowlist, public event journal, and source watermark committed in one
  transaction — plus crash recovery, terminal-fence precedence, stream
  generation switching, and `projection_lag` behavior.
- `public-execution-read`: the `PublicExecutionRead` field allowlist,
  five-state aggregate mapping and precedence, and Job/Input/Turn read
  anchoring including prepared-launch and durable-rejection observations.
- `public-session-event-stream`: per-Session public event vocabulary and
  payload allowlists, exclusive-after cursor resume, sequence ordering and
  client dedup rules, cursor validity/expiry errors, and closed-stream
  retention behavior.

## Impact

- **Server** (`packages/server`): new `/api/v1` route group and authorization
  pipeline extending `AuthResolutionMiddleware`/`MohistPrincipal`; new
  persistence for idempotency mappings and the public projection/journal
  (EF migration); a projector service consuming existing AgentJob/AgentSession
  aggregates and outboxes; composition of existing `IAgentLauncher`, follow-up,
  and stop operation paths. No new packages.
- **Docs**: `design/agent-api.md` and `design/auth.md` move from `wip` target
  behavior to shipped status; `docs/agent-api.md` and `docs/auth.md`
  implementation-gap sections and the README implementation table are updated.
- **Not affected**: existing control-plane routes, Web UI, CLI surface (PAT
  grant issuance already shipped), Runner, and Agent Connections. PATs without
  a Project grant keep their current behavior; this change adds a new boundary
  rather than changing any existing one. No breaking changes.
