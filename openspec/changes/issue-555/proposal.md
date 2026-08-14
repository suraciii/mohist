## Why

An External Agent delegating work to Mohist today has no headless path: it must borrow the Web session or the administrator file, every response loss risks launching duplicate Agent work, and a disconnect forces it to guess execution state. The target contracts in `design/agent-api.md` and `design/auth.md` are already specified (`wip`) but unimplemented — `docs/agent-api.md` explicitly warns callers not to build against it. This change ships the specified boundary so a third-party caller can authenticate with its own token, retry writes safely, and resume observation after a disconnect.

## What Changes

- Add the direct external Agent API under `/api/v1/projects/{projectId}/...` with launch, follow-up, stop, and Job/Input/Turn read routes plus a per-Session public event route. The boundary accepts a Bearer PAT only; cookies and trusted Connection identities cannot substitute.
- Extend PAT credentials with a direct-API Project grant — an explicit Project list or an explicit `operator_all` grant — and resolve every direct request to an `ExternalAgentCaller` (callerKeyId, scopes, grant). `mo auth token create` gains `--project` / `--all-projects`; a failed grant binding returns 403 and persists nothing.
- Enforce authorization before resource lookup, idempotency, and admission: an out-of-grant Project is 403 even when absent, and 401/403 paths create no mapping, rejection, outbox item, or public event.
- Make every write (launch, follow-up, stop) require an `Idempotency-Key` header. The Server normalizes the accepted request and computes the fingerprint; the first request durably maps key + fingerprint to its canonical outcome (including a durable admission rejection), a matching retry returns the same identities, and a key reused with a different payload returns `409 idempotency_key_reused` with no new effect.
- Map a keyed stop to one canonical per-target fenced stop operation with a Server-frozen target; a Turn already terminal is a durable no-op, and while a stop outcome is unknown a different key returns `409 stop_outcome_unknown` instead of superseding the unresolved effect.
- Return one strict `PublicExecutionRead` allowlist from every command and resource route, with the five-state aggregate (`accepted`, `queued`, `running`, `terminal`, `unknown`), component facts, and safe output/error only. Never expose Runner/Runtime/binding facts, prompt text, memory, or internal operation identities.
- Publish a durable, checkpointed public projection: snapshot, Session event journal, and source watermark commit in one transaction; a route whose required watermark is ahead of the projection returns `503 projection_lag` rather than stale state.
- Provide resumable per-Session public events: opaque tamper-evident cursors with strictly-after (`after`) resume, per-Session strictly increasing sequences, stream generations that reject old-generation cursors (`400 cursor_invalid`), a retained-history floor (`410 cursor_expired` with safe bounds), and documented dedup/ordering rules for out-of-order or duplicate pages.
- Flip `docs/agent-api.md` and the `design/agent-api.md` / `design/auth.md` status sections from WIP to implemented once the routes ship.

## Capabilities

- `external-agent-auth`: PAT Project grant model (explicit / operator_all), `ExternalAgentCaller` resolution for Bearer PATs, grant-aware PAT issuance in `mo auth token create`, and the authentication-before-lookup ordering (403-before-404, zero side effects on 401/403) for the direct boundary.
- `external-agent-write-idempotency`: the Idempotency-Key contract for launch, follow-up, and stop — server-side request normalization and fingerprint, durable per-scope request mappings (including durable rejections), `409 idempotency_key_reused`, and the keyed stop's mapping to a canonical fenced stop operation with the `stop_outcome_unknown` conflict rule.
- `external-agent-execution-read`: the `/api/v1` execution route surface and the `PublicExecutionRead` strict allowlist — five-state aggregate with fixed precedence, component facts, Job-anchored status recovery after a lost launch response, and the durable checkpointed projection with `503 projection_lag`.
- `external-agent-event-resume`: the persisted public Session event stream — event vocabulary, opaque cursors, exclusive-after resume, per-Session sequences, stream generations and rebuild behavior, retention floor with `cursor_expired`, closed-stream tombstones, and the caller's dedup/gap-resume rules.

## Impact

- **Server auth:** `packages/server/src/Mohist.Server/Auth` — Credential gains a Project grant, new `ExternalAgentCaller` resolution, and direct-route authorization ordering in `Auth/Identity`.
- **Server API and persistence:** new `/api/v1` route group under `packages/server/src/Mohist.Server/Api`; new durable stores (request mappings, public projection snapshots, event journal, checkpoints) under `Infrastructure/Data` with migrations. The API composes existing `IAgentLauncher`, `AgentSessionGrain`, and stop-operation grains — it adds no second execution lifecycle, queue, or event bus.
- **CLI:** `packages/cli` — `mo auth token create` grant options and validation; help text.
- **Docs:** `docs/agent-api.md`, `docs/auth.md`, and the `design/agent-api.md` / `design/auth.md` status sections move from target to shipped.
- **Dependencies:** builds on the canonical admission/capacity gate (#520), the fenced stop-operation lifecycle (#562), and durable Session/outbox facts; no new external packages. Web UI, Runner, and Agent Connections keep their own adapters and are unaffected except for shared canonical facts.
