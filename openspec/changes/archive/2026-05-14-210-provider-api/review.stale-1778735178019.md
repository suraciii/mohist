## Findings

1. Error: Existing provider route tests still assert the removed `models` field, so the test suite is not green after this contract change.
   - Evidence: `packages/cli/tests/providers.test.ts:79` still expects `provider` to have a `models` property, and `packages/cli/tests/providers.test.ts:108-119` still verifies `GET /api/providers` model IDs.
   - Reproduction: `npm test -- providers.test.ts` fails with 2 assertions against the old response shape.
   - Suggested fix: Update `packages/cli/tests/providers.test.ts` to match the new lightweight contract, or remove these assertions in favor of `/api/providers/models` coverage.

## Spec Compliance

### http-api/spec.md

- PASS `provider-api-cached-reads` / Provider state prewarmed before serving requests
  - Evidence: `packages/cli/src/server/index.ts:264-265` constructs `ProviderStateService` and awaits `providerState.warm()` before `server.start()` at `packages/cli/src/server/index.ts:317`.

- PASS `provider-api-cached-reads` / Provider list omits model IDs
  - Evidence: `packages/cli/src/services/provider-state-service.ts:5-14` defines provider list items without `models`; `packages/cli/src/api/providers.ts:62-67` serves `GET /api/providers` directly from `providerState.getProviders()`; regression assertion exists at `packages/cli/tests/provider-api-cache.test.ts:89-100`.

- PASS `provider-api-cached-reads` / Provider models preserve selectable model response shape
  - Evidence: `packages/cli/src/services/provider-state-service.ts:16-26` defines the model-group shape; `packages/cli/src/services/provider-state-service.ts:96-129` builds `{ id, name, configured, models[] }`; `packages/cli/src/api/providers.ts:121-126` serves cached groups; tests cover shape at `packages/cli/tests/provider-api-cache.test.ts:128-166`.

- PASS `provider-list-omits-models` / Provider list UI consumes lightweight providers
  - Evidence: `packages/cli/web/src/lib/provider-api.ts:3-12` removes `models` from `Provider`; `packages/cli/web/src/components/AiSettingsSection.tsx:183-201` filters/render provider cards from metadata only.

- PASS `provider-list-omits-models` / Model selectors consume model groups endpoint
  - Evidence: `packages/cli/web/src/hooks/useModels.ts:4-8` loads selectable models via `api.getAvailableModels()`; `packages/cli/web/src/components/AiSettingsSection.tsx:203-213` and `packages/cli/web/src/components/ModelSelector.tsx:103-140` consume grouped model data.

- FAIL `provider-api-performance-contract` / Regression tests protect the lightweight response contract and cache refresh behavior
  - Evidence: new regression tests exist in `packages/cli/tests/provider-api-cache.test.ts:88-285`, but the relevant existing suite is broken by stale expectations in `packages/cli/tests/providers.test.ts:79` and `packages/cli/tests/providers.test.ts:108-119`.
  - Deviation: The repository does not currently have a passing provider API test surface for the new contract.

### provider-config/spec.md

- PASS `provider-state-refresh-after-config-change` / Provider save refreshes provider state
  - Evidence: `packages/cli/src/api/providers.ts:422-430` awaits `providerState.refresh()` before returning success; cache verification test at `packages/cli/tests/provider-api-cache.test.ts:201-219`.

- PASS `provider-state-refresh-after-config-change` / Custom provider model update refreshes model groups
  - Evidence: same refresh path at `packages/cli/src/api/providers.ts:422-430`; model-group verification test at `packages/cli/tests/provider-api-cache.test.ts:221-238`.

- PASS `provider-state-refresh-after-config-change` / Provider delete refreshes provider state
  - Evidence: `packages/cli/src/api/providers.ts:461-469` awaits `providerState.refresh()` before success; provider/model delete verification at `packages/cli/tests/provider-api-cache.test.ts:240-284`.

- PASS `provider-state-refresh-after-config-change` / Existing provider change event remains emitted
  - Evidence: save path emits at `packages/cli/src/api/providers.ts:416-420`; delete path emits at `packages/cli/src/api/providers.ts:455-459`; route tests assert emission at `packages/cli/tests/providers.test.ts:163-181` and `packages/cli/tests/providers.test.ts:273-293`.

- PASS `provider-state-refresh-after-config-change` / Failed refresh preserves last good snapshot
  - Evidence: `packages/cli/src/services/provider-state-service.ts:60-133` builds fresh local `providers` and `modelGroups` arrays and only assigns snapshots at `packages/cli/src/services/provider-state-service.ts:131-132` after the rebuild completes, so a thrown error cannot partially overwrite the last good snapshot.
  - Note: no dedicated regression test covers this failure mode.

## Verification

- PASS `npm test -- provider-api-cache.test.ts`
- FAIL `npm test -- providers.test.ts`
- PASS `npm run build`

<promise>FAIL</promise>
