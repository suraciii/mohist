## Context

`cleanupPolicy` reaches the runner only as a field on `WorkDispatchResponse`
(`RunnerRoutes.cs:556`), populated in the `POST /api/runner/{id}/poll` handler
(`RunnerRoutes.cs:115`). But that handler returns `204 No Content` with **no
body** when there is no work to dispatch (`RunnerRoutes.cs:96`), so the policy
has nowhere to attach. The runner caches the policy passively from each dispatch
(`connection.ts:32`) and `runCleanupOnce` short-circuits on a null policy
(`host.ts:175` → `cleanup-loop.ts`). Net effect: cleanup never runs precisely
when the system is idle — the exact condition under which stale workspaces most
need reclaiming. Two semantically unrelated payloads (high-frequency,
intermittent "is there work" vs. low-frequency, stable "what are the cleanup
rules") share one transport unit, and the policy's availability is held hostage
by the presence of work.

The fix is a channel separation: `poll` becomes purely about work dispatch, and a
new `GET /api/runner/{id}/config` becomes the dedicated channel for runner-side
configuration. This matches the architecture boundary in `design/architecture.md`
(server owns authoritative state, runner is the execution plane; a config
endpoint is just the server pushing/pulling a decision to the runner, not a
delegation of decision authority). The change spans both planes and edits a
wire contract, hence `risk: medium`.

### Current state (verified by code reading)

- **Server.** `RunnerRoutes.cs:92-117` — `/poll` handler; `WorkDispatchResponse`
  record at `:532` carries `CleanupPolicyDto? CleanupPolicy` at `:556`;
  `ToCleanupPolicyDto(CleanupPolicyOptions)` projection at `:455`; options bound
  from `Mohist:WorkspaceCleanup` (`CleanupPolicyOptions.cs:16`). The `/poll`
  route group already exists at `RunnerRoutes.cs:18` and already injects
  `IOptions<CleanupPolicyOptions>`.
- **Runner.** `connection.ts:9` — `lastCleanupPolicy` field; `:32` — written from
  `dispatch.cleanupPolicy` inside `poll()`; `:36` — `getLastCleanupPolicy()`
  accessor; `host.ts:173-186` — `runCleanupOnce` reads the cached policy;
  `types.ts:94` — `cleanupPolicy` on `WorkDispatchResponse`; `types.ts:104` —
  reusable `CleanupPolicy` interface. URL builder at `connection.ts:302-304`
  already produces `/api/runner/{runnerId}/{path}`.
- **Tests.** Server side: `RunnerCleanupPolicyAndStatusApiSpecs.cs` exercises
  `ToCleanupPolicyDto` via the test client. Runner side: three host specs mock
  `ServerConnection` and expose `getLastCleanupPolicy = () => null`
  (`runner-host.spec.ts:57`, `runner-host-task-log.spec.ts:49`,
  `runner-host-convergence.spec.ts:46`); `cleanup-loop.spec.ts` drives
  `CleanupLoop.runOnce(policy, ...)` directly with a `StubCleanupRunner`.

### Constraints / stakeholders

- Cross server↔runner wire contract; risk **medium**.
- Single source of truth: the runner MUST NOT read `config.jsonc`; policy still
  flows server → runner.
- Must not touch `CleanupPolicyOptions` field semantics, the retention/budget
  algorithm, the cleanup-loop cadence (default 2 min), or the convergence
  backstop. Hot-reload of `config.jsonc` is #355, explicitly out of scope.
- Local single-machine deployment; no ETag/version/watch mechanisms.

## Goals / Non-Goals

**Goals:**

- **G1 — Dedicated config channel.** `GET /api/runner/{runnerId}/config` returns
  `RunnerConfigResponse { cleanupPolicy }` projected from the server's bound
  `CleanupPolicyOptions` via the existing `ToCleanupPolicyDto`, reachable
  independently of whether `poll` has work.
- **G2 — Idle-system cleanup works.** When `poll` is continuously returning 204,
  the runner still obtains a non-null policy (when configured) from `/config` and
  runs eviction on each cleanup-loop tick.
- **G3 — `poll` contract hardened to work-only.** `WorkDispatchResponse` no
  longer carries `cleanupPolicy`; the runner no longer caches policy from a
  dispatch. Work dispatch behavior is otherwise unchanged.
- **G4 — Single source of truth preserved.** Server remains authoritative;
  runner fetches policy rather than reading config files.

**Non-Goals:**

- Changing `CleanupPolicyOptions` field semantics or the eviction algorithm.
- Hot-reloading `config.jsonc` (#355).
- ETag / `If-None-Match` / watch / push-based config distribution.
- Letting the runner read `config.jsonc` directly.
- Altering the cleanup-loop period or the convergence backstop.

## Decisions

### D1 — New `GET /api/runner/{runnerId}/config`, plain periodic GET, no caching

**Decision.** Add `group.MapGet("/config", ...)` inside the existing
`/api/runner/{runnerId}` route group (`RunnerRoutes.cs:18`). The handler injects
`IOptions<CleanupPolicyOptions>` (same binding `/poll` already uses) and returns
`200 OK` with `RunnerConfigResponse { CleanupPolicy = ToCleanupPolicyDto(...) }`
unconditionally — including when the policy is fully unconfigured (all fields
`null`) and including when the system is idle. The runner calls it once per
cleanup-loop tick and **does not cache** between ticks: each `runCleanupOnce`
awaits `connection.fetchConfig(signal)` and passes the result straight into
`cleanupLoop.runOnce`.

**Rationale.** The cleanup-loop already fires every 2 minutes; one lightweight
GET per tick is negligible load and needs no invalidation logic. Avoiding a
client-side cache removes a whole class of staleness bugs (the very class that
motivated this issue) and keeps the runner trivially stateless with respect to
policy. Reusing `ToCleanupPolicyDto` + `CleanupPolicyDto` means no new field
semantics, no new sentinels, no new runner-side parser — the existing
`CleanupPolicy` TypeScript type parses the body verbatim. `GET` (not `POST`)
reflects the read-only, idempotent, cacheable-by-nature intent and matches the
spec's "plain GET with no request body" scenario.

**Alternatives considered.**

- *A1 — Keep policy on `poll`, but always return `200` with an empty-work
  sentinel instead of `204`.* Rejected: breaks the documented "no work ⇒ 204 No
  Content" contract that the runner's control flow (and the convergence backstop)
  relies on, and still conflates two concerns on one transport unit. The issue
  body explicitly calls this out as the anti-pattern to fix.
- *A2 — SignalR push of policy changes.* Rejected: overkill for a local
  single-machine system, requires the runner to be online at the moment of
  change (race on startup), and duplicates a working request/response channel.
  Pull also degrades gracefully when the server is briefly unreachable (see D4).
- *A3 — ETag / `If-None-Match` / version negotiation.* Rejected as
  over-engineering by the issue's Non-Goals; a 2-minute pull cadence already
  bounds staleness at the only resolution the cleanup loop can act on.
- *A4 — Client-side cache with TTL.* Rejected: re-introduces the staleness class
  this issue exists to remove, and adds invalidation surface (what TTL? what
  invalidates it?) for zero benefit at this cadence.

### D2 — Breaking removal of `CleanupPolicy` from `WorkDispatchResponse` on both ends

**Decision.** Remove the `CleanupPolicy` parameter from the server's
`WorkDispatchResponse` record and the `CleanupPolicy: ToCleanupPolicyDto(...)`
assignment in the `/poll` handler; remove the `cleanupPolicy` field from the
runner's `WorkDispatchResponse` type (`types.ts:94`) and the
`this.lastCleanupPolicy = dispatch.cleanupPolicy ?? null` line in `poll()`. No
compatibility shim, no deprecation grace period.

**Rationale.** Project guidance (issue Non-Goals + AC3) states no cross-version
compatibility is required; server and runner ship together as a managed pair
(`mo update`). A dual-carriage "kept for compat" field would be dead wire
traffic that confuses future readers about where policy actually lives. Removing
it outright makes the new invariant self-evident in the type: `poll` is about
work, period.

**Alternatives considered.**

- *A1 — Carry `cleanupPolicy` on both `/poll` and `/config` for a grace period.*
  Rejected: doubles the surface area for divergence, and there is no consumer
  that benefits (the runner is the only consumer and it upgrades in lockstep).
- *A2 — Keep the field but always send `null` from `/poll`.* Rejected: same dead
  wire, with the added risk that a future change re-populates it by mistake.

### D3 — Runner-side state removal: drop `lastCleanupPolicy` and `getLastCleanupPolicy()`

**Decision.** Delete the `lastCleanupPolicy` field (`connection.ts:9`) and the
`getLastCleanupPolicy()` accessor (`connection.ts:36`). `ServerConnection` gains
`fetchConfig(signal: AbortSignal): Promise<CleanupPolicy | null>` that performs
`GET /api/runner/{id}/config` and returns the `cleanupPolicy` from the parsed
body. `host.ts:runCleanupOnce` (lines 173-186) is updated to `await
this.connection.fetchConfig(signal)` instead of reading the cached accessor; the
result flows into `cleanupLoop.runOnce(policy, signal)` exactly as today.

**Rationale.** The cache was the mechanism by which the idle-gap bug hid; once
the runner pulls on demand, the cache serves no purpose. Keeping it would leave
a second, stale source of policy that could disagree with the freshly fetched
one. The three existing host test mocks
(`runner-host.spec.ts:57`,
`runner-host-task-log.spec.ts:49`,
`runner-host-convergence.spec.ts:46`)
swap `getLastCleanupPolicy = () => null` for `fetchConfig = async () => null`
(or a configured policy, where a spec needs it).

**Alternatives considered.**

- *A1 — Keep `getLastCleanupPolicy()` as a fallback for when `/config` fails.*
  Rejected: a stale fallback masks the failure and re-introduces the idle-gap
  class of bug under error conditions. D4 makes fetch failures observable and
  best-effort instead.

### D4 — `fetchConfig` is best-effort; failure skips the tick, does not throw

**Decision.** `fetchConfig` throws on non-2xx / network error (consistent with
the existing `poll()`, `report()`, `workflowRunsStatus()` helpers in
`connection.ts`). The caller `runCleanupOnce` already wraps its body in
`try/catch` and logs the error (`host.ts:182-185`); that catch now also covers
the config fetch. A failed fetch therefore logs and skips this tick — the next
tick (2 minutes later) retries. No partial state is left behind, because nothing
mutates until `cleanupLoop.runOnce` receives a non-null policy.

**Rationale.** Cleanup is already documented as best-effort
(`host.ts:168-170` comment for the convergence pass; `host.ts:183-184` for
cleanup). Making the new fetch participate in the same error posture keeps the
runner resilient to a briefly-unreachable server without inventing a new
fallback path. This also preserves the "no cached fallback" invariant from D3.

**Alternatives considered.**

- *A1 — Fall back to the last successfully fetched policy on error.* Rejected:
  re-introduces exactly the cache D3 removes, with the same staleness hazard,
  and would silently mask server-side regressions.
- *A2 — Treat fetch failure as "policy disabled" and log nothing.* Rejected:
  silent skipping is what made the original bug hard to diagnose; the spec for
  `runner-config-fetch` requires cleanup to actually run when a policy is
  configured, so a silent skip must be at least observable.

### D5 — Placement, DTO, and DI

**Decision.**

- Route: `group.MapGet("/config", ...)` in the existing `/api/runner/{runnerId}`
  group — no new group, no new auth surface; whatever gate applies to `/poll`
  applies to `/config`.
- DTO: add `public record RunnerConfigResponse(CleanupPolicyDto? CleanupPolicy)`
  next to `WorkDispatchResponse` in `RunnerRoutes.cs`. It wraps the existing
  `CleanupPolicyDto` unchanged so the runner's `CleanupPolicy` type parses it
  without modification. The wrapper exists (rather than returning `CleanupPolicyDto`
  bare) so future runner-facing config fields can be added without another
  breaking rename.
- DI: handler injects `IOptions<CleanupPolicyOptions>` exactly as `/poll` does
  today; no new service, no new grain call, no DB access. The endpoint is a pure
  projection of bound options onto a DTO.

**Rationale.** Minimal blast radius — no new infrastructure, no new dependencies,
no new auth decisions. The wrapper record preserves extensibility (a stated
benefit in the issue) at zero current cost. The `/config` endpoint does **not**
require a recent successful `/register` or `/poll`: the spec
`runner-config-endpoint` mandates it be reachable independently, matching the
"registered but never dispatched" idle scenario.

## Risks / Trade-offs

- **[Breaking wire contract on `WorkDispatchResponse`]** A stale runner binary
  hitting a new server (or vice versa) would see an unexpected/missing field.
  -> Server and runner ship together via `mo update`; no version compat is
  required by project guidance. The field is simply absent on the wire; JSON
  deserialization tolerates unknown/missing fields on both ends (System.Text.Json
  defaults; the runner's `as WorkDispatchResponse` cast).
- **[Extra GET per cleanup tick (1 req / 2 min)]** Adds a tiny amount of
  steady-state traffic. -> Negligible for a local single-machine daemon; far
  cheaper than the eviction work the loop already does. Deliberately not
  optimized with ETag/version per the issue's Non-Goals.
- **[Transient `/config` failure leaves cleanup idle for a tick]** A network
  blip or server restart skips that tick's cleanup. -> Best-effort by design
  (D4); the next tick retries. Matches the existing cleanup/convergence error
  posture. No stale fallback is kept, so a persistently-failing endpoint is
  loudly visible in logs rather than masked.
- **[`/poll` and `/config` may see different options snapshots]** If
  hot-reloading is later added (#355), a tick-window mismatch is theoretically
  possible. -> Out of scope here; both endpoints bind the same
  `IOptions<CleanupPolicyOptions>` singleton, so within the current (non-hot-
  reload) world the values are identical. #355 will own snapshot consistency.
- **[Three runner host test mocks must change]** The `getLastCleanupPolicy`
  stubs become `fetchConfig` stubs. -> Mechanical edit, isolated to test setup;
  no production behavior change hidden behind it.

## Migration Plan

- **No schema / DB / persistence migration.** Pure transport refactor; no
  domain model change (per issue "Domain Model" section).
- **Deploy order — server first, then runner.**
  1. Deploy server: adds `GET /api/runner/{id}/config`; `/poll` still emits
     `cleanupPolicy` (old runner keeps working unchanged).
  2. Deploy runner: consumes `/config`, stops reading `dispatch.cleanupPolicy`,
     and `/poll`'s field goes unused. Either side removing the field then
     becomes safe.
  3. The `WorkDispatchResponse.CleanupPolicy` removal (D2) lands together with
     the runner change in the same release so the field is removed from both
     ends atomically.
- **`mo update server` then `mo update runner`** is the supported sequence
  (per `AGENTS.md` — never `dotnet run`, to avoid runner id drift).
- **Rollback.** Revert both commits together. A partial rollback (new runner +
  old server) would have the runner call a non-existent `/config` (404) every
  tick — logged and skipped per D4 — while `/poll` work dispatch keeps working.
  A partial rollback (new server + old runner) is fully benign: the old runner
  ignores the new endpoint and keeps reading the still-present
  `WorkDispatchResponse.cleanupPolicy` until the server-side removal also rolls
  back. Recommend rollback as an atomic pair to avoid the 404 noise.

## Open Questions

- **Should `fetchConfig` carry its own per-call timeout, or rely on the
  cleanup-loop tick's `AbortSignal`?** Lean: reuse the tick signal (matches
  `poll`, `report`, etc., which all take the caller's signal). Confirm during
  implementation that the cleanup-loop tick signal stays valid for the duration
  of the fetch.
- **Should `/config` eventually expose more than `cleanupPolicy`?** Yes per the
  issue's "extensibility" note, but no other field is needed now; the
  `RunnerConfigResponse` wrapper (D5) is the only forward-facing concession.
  Adding fields later is additive and non-breaking.
