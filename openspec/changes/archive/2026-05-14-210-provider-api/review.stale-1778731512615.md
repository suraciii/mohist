## Findings

1. Error: `packages/cli/tests/provider-api-cache.test.ts:43-68` creates `tmpDir` and `configPath`, but never points `config-loader` at that file and never overrides `process.env.HOME`. `ProviderStateService.warm()` and the `POST/DELETE /api/providers/:id` handlers therefore still read and write the real `~/.mohist/config.jsonc` via `load()`/`writeConfig()` in `packages/cli/src/config/config-loader.ts:19-20,35-36,118-123`. This makes the new regression suite non-isolated, can mutate a developer's real config during test runs, and means the cache-refresh assertions are not exercising the intended temporary fixture state. Suggested fix: in `beforeEach`, save the original `HOME`, set `process.env.HOME = tmpDir` before constructing `ProviderStateService`, and restore it in `afterEach`; or refactor the route/service to accept an explicit config path dependency and use `configPath` in the test.

## Spec Compliance

### http-api/spec.md

- PASS `provider-api-cached-reads` scenario `Provider state prewarmed before serving requests`: `packages/cli/src/server/index.ts:264-265` constructs `ProviderStateService` and awaits `warm()` before registering provider routes at `:273`.
- PASS `provider-api-cached-reads` scenario `Provider list omits model IDs`: `packages/cli/src/services/provider-state-service.ts:5-14,66-94` defines/builds provider list items without a `models` field; `packages/cli/src/api/providers.ts:62-67` serves that snapshot directly.
- PASS `provider-api-cached-reads` scenario `Provider models preserve selectable model response shape`: `packages/cli/src/services/provider-state-service.ts:16-26,96-129` builds provider groups with `id`, `name`, `configured`, `models`, and each model has `id`, `name`, `badges`, `contextWindow`; `packages/cli/src/api/providers.ts:121-126` returns the cached snapshot.
- PASS `provider-list-omits-models` scenario `Provider list UI consumes lightweight providers`: `packages/cli/web/src/lib/provider-api.ts:3-12` removes `models` from `Provider`; `packages/cli/web/src/components/AiSettingsSection.tsx:183-201` uses only provider metadata for list rendering.
- PASS `provider-list-omits-models` scenario `Model selectors consume model groups endpoint`: `packages/cli/web/src/hooks/useModels.ts:4-8` loads `/providers/models` through `packages/cli/web/src/lib/api.ts:191`, and `packages/cli/web/src/components/AiSettingsSection.tsx:203-213,291-311` builds selectors from that data.
- FAIL `provider-api-performance-contract` scenarios: regression tests exist in `packages/cli/tests/provider-api-cache.test.ts:71-268`, but the suite is not safely isolated from the real config path because of the issue above, so cache-refresh coverage is not reliable.

### provider-config/spec.md

- PASS `provider-state-refresh-after-config-change` scenario `Provider save refreshes provider state`: `packages/cli/src/api/providers.ts:402-430` writes config, emits the existing event, then awaits `providerState.refresh()` before returning success.
- PASS `provider-state-refresh-after-config-change` scenario `Custom provider model update refreshes model groups`: same `POST /:id` path refreshes the shared cache before responding, and model groups are rebuilt from `customProviders[id]?.models` in `packages/cli/src/services/provider-state-service.ts:114-129`.
- PASS `provider-state-refresh-after-config-change` scenario `Provider delete refreshes provider state`: `packages/cli/src/api/providers.ts:451-469` deletes config, emits the event, awaits `providerState.refresh()`, then returns success.
- PASS `provider-state-refresh-after-config-change` scenario `Existing provider change event remains emitted`: `packages/cli/src/api/providers.ts:416-420,455-459` still emits `config:providers:changed` on save and delete.
- PASS `provider-state-refresh-after-config-change` scenario `Failed refresh preserves last good snapshot`: `packages/cli/src/services/provider-state-service.ts:60-133` builds new arrays first and only assigns `this.providersSnapshot`/`this.modelsSnapshot` after the rebuild completes, so failed rebuilds cannot partially replace the old snapshot.

## Quality Notes

- Complexity is acceptable in the new service and route changes, though `rebuildSnapshots()` in `packages/cli/src/services/provider-state-service.ts:60-133` is longer than the preferred 50-line target.
- Targeted verification passed: `npm test -- provider-api-cache.test.ts` and `npm run build` in `packages/cli` both succeeded.

<promise>FAIL</promise>
