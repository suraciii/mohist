## Context

`GET /api/providers` currently loads config, resolves all built-in providers, and calls `getModelsByProvider()` for every provider on each request. With the current models.dev snapshot this serializes about 4,371 model IDs into the provider list response, producing a 177KB payload for data that the AI settings provider list does not use.

`GET /api/providers/models` is the endpoint that actually serves selectable model metadata. Both endpoints duplicate provider/config/model aggregation work in `packages/cli/src/api/providers.ts`, so the fix should centralize that work and expose precomputed read snapshots to the API routes.

## Goals / Non-Goals

**Goals:**
- Serve `GET /api/providers` from memory and remove the `models` field from each provider item.
- Serve `GET /api/providers/models` from the same in-memory provider state.
- Prewarm provider state before the HTTP server starts accepting requests.
- Refresh provider state after provider config create/update/delete operations.
- Preserve existing provider configuration and model selection behavior.

**Non-Goals:**
- Do not change provider registry resolution rules or models.dev parsing semantics.
- Do not add persistent cache storage or cache invalidation across processes.
- Do not change `POST /api/providers/:id`, `DELETE /api/providers/:id`, or `POST /api/providers/test` behavior beyond refreshing the in-memory state after successful config writes.
- Do not make the provider/model endpoints fetch live provider APIs.

## Decisions

### D1: Add `ProviderStateService` as the cache owner

Create a service under `packages/cli/src/services/` that owns two immutable snapshots:

- provider list items for `GET /api/providers`, excluding model IDs
- provider model groups for `GET /api/providers/models`

The service should expose a small interface such as `warm()`, `refresh()`, `getProviders()`, and `getProviderModelGroups()`. `refresh()` rebuilds both snapshots by reading the current config, resolving built-in providers, and reusing `getProviderConfig()` / `getModelsByProvider()` as the source of truth.

**Alternatives considered:**
- Cache inside `api/providers.ts`: smaller edit, but keeps aggregation and cache invalidation mixed into route handlers.
- Cache only `getModelsByProvider()` results: helps repeated model loading but does not solve the oversized `/api/providers` contract or duplicated endpoint assembly.

### D2: Build complete snapshots, not endpoint fragments

`ProviderStateService` should return fully shaped API data, not raw provider registries plus config. Routes should only wrap snapshots in `ApiResponse` and handle errors.

This keeps model/config aggregation knowledge in one module and prevents the route layer from duplicating details like default provider detection, API key masking, custom provider formatting, configured source mapping, and model metadata shaping.

**Alternatives considered:**
- Return lower-level provider registry data and map in each endpoint: more flexible, but preserves the current duplication and makes future response changes harder.
- Split into separate provider-list and model-list services: unnecessary for two snapshots built from the same inputs and refreshed on the same events.

### D3: Prewarm synchronously during server startup

Instantiate `ProviderStateService` in `server/index.ts`, call `await providerState.warm()` before registering or starting the HTTP server, then pass the service into `createProviderRoutes()`.

If prewarm fails, server startup should fail with the existing top-level startup error path. A half-warmed provider state would make settings behavior unpredictable and hide startup/config errors until the first user request.

**Alternatives considered:**
- Lazy warm on first request: preserves startup time but leaves the first settings load slow, which is the user-visible problem.
- Warm in the background after startup: can serve stale or empty data during startup and requires extra readiness states.

### D4: Refresh after successful config writes, with stale-on-refresh-failure reads

After `writeConfig()` succeeds in provider create/update/delete handlers, call `await providerState.refresh()` before returning success. Continue emitting `config:providers:changed` for existing consumers such as agent runtime invalidation.

For robustness, `refresh()` should only replace the current snapshots after a full rebuild succeeds. If a later refresh fails, existing read endpoints can continue serving the last good snapshot while the write handler returns an error if the refresh failure happened inline after that write.

**Alternatives considered:**
- Refresh only from the event-bus listener: decoupled, but event emitters are synchronous in current usage and do not provide a clear way for the write request to know the cache is current before responding.
- Mutate the cache incrementally from the changed provider payload: faster but duplicates provider resolution logic and is error-prone for default provider, custom providers, and model list changes.

### D5: Update frontend provider types to match the lighter response

Remove `models` from the web `Provider` type used by provider list UI. Any UI needing selectable models should continue using `api.getAvailableModels()` / `GET /api/providers/models`.

**Alternatives considered:**
- Keep `models: []` in `GET /api/providers` for backward compatibility: avoids a frontend type change but retains a misleading response field and weakens the contract that model data belongs to `/providers/models`.

## Risks / Trade-offs

- [Provider data can become stale if config changes outside API writes] → Accept this for now because existing config writes flow through the API; server restart or future config-file watch can refresh external edits.
- [Refresh adds latency to provider save/delete responses] → The refresh cost moves to infrequent writes and keeps frequent settings reads fast.
- [Returning last good snapshots may hide refresh failures on reads] → Log refresh failures and surface inline refresh errors during provider write requests; this protects read availability without silently accepting failed writes.
- [Removing `models` is a response contract change] → Update in-repo web consumers and specs; the field was not used by the AI settings provider list and selectable models remain available through `/api/providers/models`.

## Migration Plan

1. Add `ProviderStateService` with snapshot rebuild logic copied from the current provider list and models endpoints, then remove model IDs from the provider-list item shape.
2. Instantiate and prewarm the service in `server/index.ts` before server start.
3. Change `createProviderRoutes()` to accept `ProviderStateService` and read snapshots for `GET /api/providers` and `GET /api/providers/models`.
4. Refresh the service after successful provider config create/update/delete operations.
5. Update web provider types and any provider-list consumers to stop reading `provider.models` from `/api/providers`.
6. Add or update tests for lightweight provider response shape, cached model response behavior, and refresh after config mutation.

Rollback is straightforward: route handlers can be restored to their previous per-request aggregation logic and the server can stop constructing `ProviderStateService`. No persisted data migration is involved.

## Open Questions

None.
