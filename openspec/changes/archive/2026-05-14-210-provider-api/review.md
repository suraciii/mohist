## Findings

1. Warning: `packages/cli/src/api/providers.ts:60-191`
   The route module still contains the full legacy aggregation fallback for both `GET /api/providers` and `GET /api/providers/models`. Production startup wires `ProviderStateService` in `packages/cli/src/server/index.ts:264-273`, so behavior is currently correct, but this leaves duplicate shaping logic in two places and creates future drift risk.
   Suggested change: make `providerState` required in `createProviderRoutes()` and remove the fallback aggregation branches so the route layer always reads snapshots from `ProviderStateService`.

2. Warning: `packages/cli/tests/provider-api-cache.test.ts:200-285`
   Regression tests cover save/delete refresh and response shape, but they do not exercise the failure path required by `provider-config/spec.md` where a refresh error must preserve the last good snapshot.
   Suggested change: add a test that warms `ProviderStateService`, forces a later `refresh()` rebuild failure, and verifies subsequent reads still return the previous snapshot.

## Spec Compliance

### `http-api/spec.md`

- PASS `provider-api-cached-reads` / prewarm before serving requests
  Evidence: `packages/cli/src/server/index.ts:264-265` constructs `ProviderStateService` and awaits `providerState.warm()` before routes are mounted and before `server.start()` at `packages/cli/src/server/index.ts:317`.

- PASS `provider-api-cached-reads` / provider list omits model IDs
  Evidence: `packages/cli/src/services/provider-state-service.ts:5-14` defines provider list items without `models`; `packages/cli/src/api/providers.ts:62-67` returns `providerState.getProviders()`; `packages/cli/tests/provider-api-cache.test.ts:89-100` verifies each provider item does not include `models`.

- PASS `provider-api-cached-reads` / provider models preserve selectable model response shape
  Evidence: `packages/cli/src/services/provider-state-service.ts:16-26` defines groups with `id`, `name`, `configured`, `models`; `packages/cli/src/services/provider-state-service.ts:105-109` and `122-126` shape each model with `id`, `name`, `badges`, `contextWindow`; `packages/cli/src/api/providers.ts:121-126` returns cached groups from provider state; `packages/cli/tests/provider-api-cache.test.ts:128-166` verifies the response shape.

- PASS `provider-list-omits-models` / provider list UI consumes lightweight providers
  Evidence: `packages/cli/web/src/lib/provider-api.ts:3-12` removes `models` from the web `Provider` type; `packages/cli/web/src/components/AiSettingsSection.tsx:183-201` filters/render provider list using metadata only; connection state, source, default badge, and masked key render at `packages/cli/web/src/components/AiSettingsSection.tsx:89-99`.

- PASS `provider-list-omits-models` / model selectors consume model groups endpoint
  Evidence: `packages/cli/web/src/hooks/useModels.ts:4-8` loads selectable models from `api.getAvailableModels()`; `packages/cli/web/src/components/AiSettingsSection.tsx:203-213` derives available models from `modelProviders[*].models`; `packages/cli/web/src/lib/api.ts:191` maps that call to `/providers/models`.

- PASS `provider-api-performance-contract` / lightweight provider response tested
  Evidence: `packages/cli/tests/provider-api-cache.test.ts:88-100`.

- PASS `provider-api-performance-contract` / cached provider model response tested
  Evidence: `packages/cli/tests/provider-api-cache.test.ts:127-198`.

- PASS `provider-api-performance-contract` / cached state refresh tested
  Evidence: `packages/cli/tests/provider-api-cache.test.ts:200-285` covers POST and DELETE refresh behavior for both provider list and model groups.

### `provider-config/spec.md`

- PASS `provider-state-refresh-after-config-change` / provider save refreshes provider state
  Evidence: `packages/cli/src/api/providers.ts:422-429` awaits `providerState.refresh()` before returning success; `packages/cli/tests/provider-api-cache.test.ts:201-219` verifies subsequent `GET /api/providers` includes the new provider.

- PASS `provider-state-refresh-after-config-change` / custom provider model update refreshes model groups
  Evidence: `packages/cli/src/api/providers.ts:422-429` refreshes inline after save; `packages/cli/tests/provider-api-cache.test.ts:221-238` verifies subsequent `GET /api/providers/models` reflects the saved custom model list.

- PASS `provider-state-refresh-after-config-change` / provider delete refreshes provider state
  Evidence: `packages/cli/src/api/providers.ts:461-469` awaits `providerState.refresh()` before returning success; `packages/cli/tests/provider-api-cache.test.ts:240-284` verifies both provider list and model groups reflect deletion.

- PASS `provider-state-refresh-after-config-change` / existing provider change event remains emitted
  Evidence: `packages/cli/src/api/providers.ts:416-420` emits `config:providers:changed` on save and `packages/cli/src/api/providers.ts:455-458` emits it on delete.

- PASS `provider-state-refresh-after-config-change` / failed refresh preserves last good snapshot
  Evidence: `packages/cli/src/services/provider-state-service.ts:60-133` builds new local `providers` and `modelGroups` arrays and only assigns them to `this.providersSnapshot` / `this.modelsSnapshot` at the end, so a thrown rebuild leaves the previous snapshot intact.

## Verification

- PASS `npm test -- provider-api-cache.test.ts`
- PASS `npm run build`

## Verdict

Implementation matches the proposal and required behavior. No error-level correctness issues found. The remaining concerns are maintainability and a missing negative-path regression test.

<promise>PASS</promise>
