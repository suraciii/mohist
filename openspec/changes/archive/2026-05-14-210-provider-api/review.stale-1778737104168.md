## Findings

### Error

1. `packages/cli/tests/providers.test.ts:68-120` still asserts the old `/api/providers` contract and currently fails. The suite expects every provider item to contain `models` and verifies fully-qualified model IDs from that endpoint, but the new implementation removes `models` by design. Running `npm run test -- tests/provider-api-cache.test.ts tests/providers.test.ts tests/model-selection.test.ts` fails with 2 failing assertions in this file, so the change does not meet the requirement that regression tests pass.

2. `packages/cli/src/api/providers.ts:60-109` and `packages/cli/src/api/providers.ts:119-183` still contain a full legacy rebuild path when `providerState` is omitted. That path re-reads config, resolves builtins, and rebuilds provider/model responses inside the route handler, which violates the `provider-api-cached-reads` requirement that reads come from prewarmed in-memory provider state rather than being rebuilt independently in the handler. Production wiring passes `providerState` in `packages/cli/src/server/index.ts:264-273`, but the route contract itself still permits the forbidden behavior.

## Spec Compliance

### `http-api/spec.md`

- PASS `Provider state prewarmed before serving requests`
  Evidence: `packages/cli/src/server/index.ts:264-265` constructs `ProviderStateService` and awaits `providerState.warm()` before registering provider routes and before `server.start()` at `packages/cli/src/server/index.ts:317`.

- PASS `Provider list omits model IDs`
  Evidence: `packages/cli/src/services/provider-state-service.ts:66-79` and `:81-94` build `ProviderListItem` snapshots with `id`, `name`, `baseURL`, `configured`, `source`, `isBuiltin`, `isDefault`, and `apiKeyMasked`, and no `models` field. `packages/cli/src/api/providers.ts:62-67` returns that snapshot directly.

- FAIL `Provider models preserve selectable model response shape and are read from provider state rather than rebuilt independently in the route handler`
  Evidence: the cached path is correct at `packages/cli/src/api/providers.ts:121-126`, but the same handler still has an independent rebuild implementation at `packages/cli/src/api/providers.ts:129-183`.

- PASS `Provider list UI consumes lightweight providers`
  Evidence: `packages/cli/web/src/lib/provider-api.ts:3-12` removes `models` from the `Provider` type used by `useProviders()` in `packages/cli/web/src/hooks/useQueries.ts:273-277`. `packages/cli/web/src/components/AiSettingsSection.tsx:183-201` renders provider lists from metadata fields only.

- PASS `Model selectors consume model groups endpoint`
  Evidence: `packages/cli/web/src/hooks/useModels.ts:5-7` calls `api.getAvailableModels()`, and `packages/cli/web/src/lib/api.ts:191` maps that to `GET /api/providers/models`. `packages/cli/web/src/components/AiSettingsSection.tsx:203-213` and `packages/cli/web/src/components/ModelSelector.tsx:103-139` both read selectable models from that grouped endpoint.

- PASS `Lightweight provider response is tested`
  Evidence: `packages/cli/tests/provider-api-cache.test.ts:88-100` asserts `GET /api/providers` items do not include `models`.

- PASS `Cached provider model response is tested`
  Evidence: `packages/cli/tests/provider-api-cache.test.ts:127-197` verifies the `/api/providers/models` response shape and model fields.

- PASS `Cached state refresh is tested`
  Evidence: `packages/cli/tests/provider-api-cache.test.ts:200-284` verifies POST and DELETE mutations refresh subsequent provider/model reads.

### `provider-config/spec.md`

- PASS `Provider save refreshes provider state`
  Evidence: `packages/cli/src/api/providers.ts:422-430` awaits `providerState.refresh()` before returning success from `POST /api/providers/:id`.

- PASS `Custom provider model update refreshes model groups`
  Evidence: the same refresh path at `packages/cli/src/api/providers.ts:422-424` applies to custom provider model updates, and `packages/cli/tests/provider-api-cache.test.ts:221-238` verifies subsequent `/api/providers/models` reads reflect updated custom models.

- PASS `Provider delete refreshes provider state`
  Evidence: `packages/cli/src/api/providers.ts:461-469` awaits `providerState.refresh()` before returning success from `DELETE /api/providers/:id`.

- PASS `Existing provider change event remains emitted`
  Evidence: `packages/cli/src/api/providers.ts:416-420` and `:455-458` still emit `config:providers:changed` on save and delete.

- PASS `Failed refresh preserves last good snapshot`
  Evidence: `packages/cli/src/services/provider-state-service.ts:60-133` builds new `providers` and `modelGroups` locals and only assigns `this.providersSnapshot` and `this.modelsSnapshot` at the end, so a thrown error cannot partially replace the previous snapshot.

## Complexity

- PASS `ProviderStateService` methods are small. `warm()`, `refresh()`, `getProviders()`, and `getProviderModelGroups()` are trivial, and `rebuildSnapshots()` is 74 lines with simple sequential logic. No obvious cyclomatic spike beyond the two provider loops.

## Security

- PASS Input validation and masking remain intact. `POST /api/providers/:id` still validates IDs, URLs, and required fields at `packages/cli/src/api/providers.ts:326-382`, and API key masking remains in `packages/cli/src/services/provider-state-service.ts:28-31`.

## Test Results

- `npm run typecheck`: PASS
- `npm run test -- tests/provider-api-cache.test.ts tests/providers.test.ts tests/model-selection.test.ts`: FAIL
  Evidence: `tests/providers.test.ts` has 2 failing assertions because it still expects the removed `models` field.

## Fix Suggestions

1. Update `packages/cli/tests/providers.test.ts:68-120` to match the new lightweight `/api/providers` contract.
   Suggested change: remove assertions for `provider.models`, replace them with `expect(provider).not.toHaveProperty('models')`, and move fully-qualified model ID assertions to `/api/providers/models` if that behavior still needs coverage.

2. Tighten `packages/cli/src/api/providers.ts:39-183` so provider read routes require `ProviderStateService` instead of silently rebuilding on demand.
   Suggested change: make `providerState` a required argument for `createProviderRoutes`, delete the fallback aggregation branches in `GET /` and `GET /models`, and let tests instantiate/warm a `ProviderStateService` explicitly just like production does.

<promise>FAIL</promise>
