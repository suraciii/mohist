## Findings

1. High: `createProviderRoutes()` still supports the old uncached slow path, so the provider read endpoints are not actually constrained to provider-state-backed reads.
File: `packages/cli/src/api/providers.ts:39-183`
Evidence: `providerState` is optional in `createProviderRoutes(eventBus?: EventBus, rateLimiter?: RateLimiter, providerState?: ProviderStateService)`, and both `GET /api/providers` (`lines 70-109`) and `GET /api/providers/models` (`lines 129-183`) still rebuild provider/config/model data inside the route handler when `providerState` is absent.
Impact: This violates the changed contract in `specs/http-api/spec.md#provider-api-cached-reads`, especially “the response is read from provider state rather than rebuilt independently in the route handler” and the task acceptance criterion that the endpoints no longer rebuild all provider/model data inside route handlers on every request. The production server currently passes `providerState`, but the route API still permits and contains the non-compliant behavior.
Suggested fix: Make `providerState` required in `createProviderRoutes()`, remove the fallback rebuild branches from both read endpoints, and fail fast if a caller tries to construct provider routes without a warmed `ProviderStateService`.

2. Warning: `ProviderStateService` exposes mutable snapshot arrays directly.
File: `packages/cli/src/services/provider-state-service.ts:52-57`
Evidence: `getProviders()` and `getProviderModelGroups()` return `this.providersSnapshot` and `this.modelsSnapshot` directly.
Impact: Any caller that mutates the returned arrays or nested objects can corrupt the in-memory cache and break the “last good snapshot” guarantee implicitly relied on by the design.
Suggested fix: Return defensive copies or readonly snapshots from the getters.

## Spec Compliance

### http-api/spec.md

#### Requirement: provider-api-cached-reads

- Scenario: Provider state prewarmed before serving requests
PASS
Evidence: `packages/cli/src/server/index.ts:264-265` constructs `ProviderStateService` and awaits `providerState.warm()` before `server.addRouter('/api/providers', ...)` and before `await server.start()` at `line 317`.

- Scenario: Provider list omits model IDs
PASS
Evidence: `packages/cli/src/services/provider-state-service.ts:5-14` defines `ProviderListItem` without `models`; `packages/cli/src/api/providers.ts:63-66` returns `providerState.getProviders()`; regression test `packages/cli/tests/provider-api-cache.test.ts:88-100` asserts every provider item lacks a `models` field.

- Scenario: Provider models preserve selectable model response shape
FAIL
Evidence: The shape itself is preserved by `packages/cli/src/services/provider-state-service.ts:16-26,96-129` and tested in `packages/cli/tests/provider-api-cache.test.ts:127-197`, but `packages/cli/src/api/providers.ts:119-183` still contains an alternate route-handler code path that rebuilds model groups independently when `providerState` is omitted.
Deviation: The spec requires the response to be read from provider state rather than rebuilt independently in the route handler.

#### Requirement: provider-list-omits-models

- Scenario: Provider list UI consumes lightweight providers
PASS
Evidence: `packages/cli/web/src/lib/provider-api.ts:3-12` removes `models` from the `Provider` type. `packages/cli/web/src/components/AiSettingsSection.tsx:183-201` filters and renders providers using metadata fields only (`configured`, `isBuiltin`, `name`, `id`) and does not read `provider.models`.

- Scenario: Model selectors consume model groups endpoint
PASS
Evidence: `packages/cli/web/src/hooks/useModels.ts:4-8` loads model options via `api.getAvailableModels()`, and `packages/cli/web/src/lib/api.ts:191` maps that to `GET /api/providers/models`. `packages/cli/web/src/components/AiSettingsSection.tsx:203-213,291-295` builds selectable models from `modelProviders[].models`.

#### Requirement: provider-api-performance-contract

- Scenario: Lightweight provider response is tested
PASS
Evidence: `packages/cli/tests/provider-api-cache.test.ts:88-100` verifies `GET /api/providers` items do not include `models`.

- Scenario: Cached provider model response is tested
PASS
Evidence: `packages/cli/tests/provider-api-cache.test.ts:127-197` verifies the `/api/providers/models` response shape, including `id`, `name`, `badges`, and `contextWindow`.

- Scenario: Cached state refresh is tested
PASS
Evidence: `packages/cli/tests/provider-api-cache.test.ts:200-284` verifies create/update and delete flows refresh subsequent provider and model reads.

### provider-config/spec.md

#### Requirement: provider-state-refresh-after-config-change

- Scenario: Provider save refreshes provider state
PASS
Evidence: `packages/cli/src/api/providers.ts:402-424` writes config, emits the change event, and awaits `providerState.refresh()` before returning success. `packages/cli/tests/provider-api-cache.test.ts:201-219` verifies subsequent `GET /api/providers` reflects the new provider.

- Scenario: Custom provider model update refreshes model groups
PASS
Evidence: `packages/cli/src/api/providers.ts:389-424` persists `models` and refreshes provider state before returning. `packages/cli/tests/provider-api-cache.test.ts:221-238` verifies subsequent `GET /api/providers/models` reflects the updated custom models.

- Scenario: Provider delete refreshes provider state
PASS
Evidence: `packages/cli/src/api/providers.ts:451-463` deletes config and awaits `providerState.refresh()` before success. `packages/cli/tests/provider-api-cache.test.ts:240-284` verifies subsequent provider and model reads reflect deletion.

- Scenario: Existing provider change event remains emitted
PASS
Evidence: `packages/cli/src/api/providers.ts:416-420,455-459` still emits `config:providers:changed`. Existing regression coverage in `packages/cli/tests/providers.test.ts:163-181` and `packages/cli/tests/providers.test.ts:273-293` verifies the event is emitted for save and delete.

- Scenario: Failed refresh preserves last good snapshot
PASS
Evidence: `packages/cli/src/services/provider-state-service.ts:60-133` rebuilds into local `providers` and `modelGroups` variables and only assigns `this.providersSnapshot` / `this.modelsSnapshot` after the full rebuild succeeds, so a thrown error cannot partially replace the prior snapshot.

## Verification

- `npm test -- provider-api-cache.test.ts useProviderGroups.test.ts` in `packages/cli`: PASS
- `npm run build` in `packages/cli`: PASS

<promise>FAIL</promise>
