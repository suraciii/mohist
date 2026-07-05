## Why

The `cleanupPolicy` reaches the runner only as a field on `WorkDispatchResponse`, but `POST /api/runner/{id}/poll` returns `204 No Content` (no body) when there is no work to dispatch (`RunnerRoutes.cs:96`). So whenever the system is idle — exactly when old workspaces most need reclaiming — the runner's `lastCleanupPolicy` stays `null`, `cleanup-loop.ts:43` short-circuits, and cleanup never runs. Two semantically unrelated things (high-frequency "is there work" vs. low-frequency stable "what are the cleanup rules") are bound to one transport unit, and the policy's availability is held hostage by the presence of work.

## What Changes

- Add a dedicated runner config channel: `GET /api/runner/{id}/config` returns a new `RunnerConfigResponse { cleanupPolicy }`, sourced from the server's bound `CleanupPolicyOptions` (reusing the existing `CleanupPolicyDto` shape and `ToCleanupPolicyDto` mapping). The server remains the single source of truth; the runner does not read `config.jsonc`.
- Runner `ServerConnection` gains a `fetchConfig()` method; `host.ts:runCleanupOnce` calls it on every cleanup-loop tick (default 2 min) instead of reading a cached `getLastCleanupPolicy()`. This makes cleanup execute whenever the loop fires, regardless of whether work was dispatched.
- Remove the `lastCleanupPolicy` field and `getLastCleanupPolicy()` accessor from `connection.ts`; the runner no longer caches policy passively from dispatch.
- **BREAKING** (wire contract): remove `CleanupPolicy` from `WorkDispatchResponse` on both server (`RunnerRoutes.cs:556`, and the `CleanupPolicy: ToCleanupPolicyDto(...)` assignment at `RunnerRoutes.cs:115`) and runner (`types.ts:94`). No version compatibility is required per project guidance, so the field is removed outright rather than carried as a duplicate. `poll`'s work-dispatch behavior is otherwise unchanged.
- No ETag/version/watch mechanism; plain periodic GET. No change to `CleanupPolicyOptions` field semantics, the retention/budget algorithm in `cleanup-loop.ts`, the cleanup-loop period, or the convergence backstop.

## Capabilities
- `runner-config-endpoint`: The server exposes `GET /api/runner/{id}/config` returning `RunnerConfigResponse { cleanupPolicy }` derived from `CleanupPolicyOptions`, available independently of whether any work is dispatchable.
- `runner-config-fetch`: The runner fetches its config from the new endpoint on each cleanup-loop cycle and drives cleanup from the fetched policy, so workspace cleanup runs even when `poll` is continuously returning 204 (no work dispatched).
- `poll-policy-decoupling`: `POST /api/runner/{id}/poll` and `WorkDispatchResponse` no longer carry `cleanupPolicy`; the runner no longer reads or caches policy from a dispatch. Work dispatch becomes purely about work.

## Impact

- **Server** (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs`): add the `MapGet("/config", ...)` route returning `RunnerConfigResponse`; add the `RunnerConfigResponse(CleanupPolicyDto? CleanupPolicy)` record; remove the `CleanupPolicy` parameter from `WorkDispatchResponse` and its assignment in the `/poll` handler. `ToCleanupPolicyDto` stays (now shared by both shapes until the poll field is gone, then used only by `/config`).
- **Runner** (`packages/runner/src`):
  - `server/connection.ts` — add `fetchConfig(signal): Promise<CleanupPolicy | null>`; remove `lastCleanupPolicy` field and `getLastCleanupPolicy()`; drop the `this.lastCleanupPolicy = dispatch.cleanupPolicy ?? null` line from `poll()`.
  - `runtime/host.ts` — `runCleanupOnce` awaits `this.connection.fetchConfig(signal)` and passes the result to `cleanupLoop.runOnce`.
  - `core/types.ts` — add `RunnerConfigResponse`; remove `cleanupPolicy` from `WorkDispatchResponse`.
- **Tests**: server spec for the new `/config` endpoint (policy shape + null/disabled sentinels); runner spec that cleanup runs when `poll` returns 204 and `/config` returns a policy (idle-system behavior); existing poll/dispatch specs updated to no longer expect a `cleanupPolicy` field. All per `design/testing.md` — no real HTTP (server uses test client / `WebApplicationFactory`; runner uses fake connection), no real time.
- **No domain model, DB, or external-dependency changes.** Risk (medium): the change spans both planes and is a breaking wire-contract edit on `WorkDispatchResponse`; mitigation is that poll's dispatch logic is untouched and the new `/config` channel is additive on the server side.
