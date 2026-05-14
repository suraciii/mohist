# Review Report

## Findings

1. ERROR: `GET /api/providers` and `GET /api/providers/models` still contain full per-request rebuild paths, so the route layer does not fully enforce the cached-read contract from the spec.
File: `packages/cli/src/api/providers.ts:60-109`, `packages/cli/src/api/providers.ts:119-183`
Evidence: when `providerState` is absent, both handlers call `load()`, `getBuiltinProviders()`, `getProviderConfig()`, and `getModelsByProvider()` directly inside the route handler.
Impact: this violates `provider-api-cached-reads` / `provider-api-performance-contract` intent that reads come from shared provider state rather than being rebuilt independently in the route layer.
Suggested fix: make `ProviderStateService` required in `createProviderRoutes()` and remove the non-cached fallback branches, or fail route construction if the service is missing.

2. ERROR: the existing provider route regression suite is still asserting the removed `models` field on `GET /api/providers`, and currently fails.
File: `packages/cli/tests/providers.test.ts:67-120`
Evidence: the test still expects `provider.models` at lines 79 and 114-119. Running `npm test -- provider-api-cache.test.ts providers.test.ts` fails with 2 failing assertions in this file.
Impact: the implementation does not satisfy the task requirement that regression coverage pass after the response contract change.
Suggested fix: update `packages/cli/tests/providers.test.ts` to assert the lightweight response contract instead of the removed `models` field, and keep model-shape assertions on `GET /api/providers/models` only.

## Dimensions

### Correctness: FAIL

- The production server wiring does prewarm and pass `ProviderStateService` correctly (`packages/cli/src/server/index.ts:264-273`), and config writes refresh before success responses (`packages/cli/src/api/providers.ts:422-430`, `461-469`).
- But the route module still supports non-cached read execution paths (`packages/cli/src/api/providers.ts:60-109`, `119-183`), which is a direct spec/design deviation.

### Complexity: PASS

- `ProviderStateService.rebuildSnapshots()` is 74 lines (`packages/cli/src/services/provider-state-service.ts:60-133`), a bit larger than the requested under-50 guideline, but logic remains linear and understandable.
- No serious complexity risk found beyond some duplication between `provider-state-service.ts` and the route fallback paths.

### Test Coverage: FAIL

- New regression coverage exists in `packages/cli/tests/provider-api-cache.test.ts:88-285` for lightweight provider responses, model-group shape, and refresh after mutations.
- The relevant route suite is still broken: `npm test -- provider-api-cache.test.ts providers.test.ts` fails because `packages/cli/tests/providers.test.ts:79` and `114-119` still expect `models` on `GET /api/providers`.
- I found no automated test covering the failed-refresh stale-snapshot scenario from `provider-config/spec.md`.

### Security: PASS

- No new injection or secret-exposure issue found in the reviewed change.
- API key masking remains in place in cached snapshots (`packages/cli/src/services/provider-state-service.ts:28-31`, `77`, `92`).

## Spec Compliance

### `http-api/spec.md`

#### Requirement: `provider-api-cached-reads`

- Scenario: Provider state prewarmed before serving requests
Result: PASS
Evidence: `packages/cli/src/server/index.ts:264-265` constructs `ProviderStateService` and awaits `providerState.warm()` before routers are attached and before `server.start()` at line 317.

- Scenario: Provider list omits model IDs
Result: PASS
Evidence: cached provider items are shaped without `models` in `packages/cli/src/services/provider-state-service.ts:5-14`, `66-94`; the lightweight regression test also asserts absence of `models` in `packages/cli/tests/provider-api-cache.test.ts:88-100`.

- Scenario: Provider models preserve selectable model response shape
Result: PASS
Evidence: cached model groups include `id`, `name`, `configured`, and `models`, and model items include `id`, `name`, `badges`, `contextWindow` in `packages/cli/src/services/provider-state-service.ts:16-26`, `96-128`; verified by `packages/cli/tests/provider-api-cache.test.ts:127-190`.

- Scenario: response is read from provider state rather than rebuilt independently in the route handler
Result: FAIL
Evidence: `packages/cli/src/api/providers.ts:70-109` and `129-183` still rebuild data directly when `providerState` is not passed.

#### Requirement: `provider-list-omits-models`

- Scenario: Provider list UI consumes lightweight providers
Result: PASS
Evidence: the web `Provider` type no longer includes `models` in `packages/cli/web/src/lib/provider-api.ts:3-12`; provider-list grouping uses only provider metadata in `packages/cli/web/src/hooks/useProviderGroups.ts:23-28`.

- Scenario: Model selectors consume model groups endpoint
Result: PASS
Evidence: AI settings build selectable models from `modelProviders` in `packages/cli/web/src/components/AiSettingsSection.tsx:203-213`; model selector also reads grouped providers from `useModels()` and iterates `provider.models` there in `packages/cli/web/src/components/ModelSelector.tsx:103-139`.

#### Requirement: `provider-api-performance-contract`

- Scenario: Lightweight provider response is tested
Result: PASS
Evidence: `packages/cli/tests/provider-api-cache.test.ts:88-100` verifies provider items do not include `models`.

- Scenario: Cached provider model response is tested
Result: PASS
Evidence: `packages/cli/tests/provider-api-cache.test.ts:127-190` verifies model-group and model-item shape.

- Scenario: Cached state refresh is tested
Result: PASS
Evidence: `packages/cli/tests/provider-api-cache.test.ts:200-285` verifies cache refresh after create and delete mutations.

### `provider-config/spec.md`

#### Requirement: `provider-state-refresh-after-config-change`

- Scenario: Provider save refreshes provider state
Result: PASS
Evidence: `packages/cli/src/api/providers.ts:422-430` awaits `providerState.refresh()` before returning success; `packages/cli/tests/provider-api-cache.test.ts:201-219` verifies the subsequent provider read reflects the new provider.

- Scenario: Custom provider model update refreshes model groups
Result: PASS
Evidence: `packages/cli/src/api/providers.ts:422-430` refreshes after save; `packages/cli/tests/provider-api-cache.test.ts:221-238` verifies subsequent `GET /api/providers/models` reflects updated custom models.

- Scenario: Provider delete refreshes provider state
Result: PASS
Evidence: `packages/cli/src/api/providers.ts:461-469` awaits refresh before success; `packages/cli/tests/provider-api-cache.test.ts:240-284` verifies subsequent provider and model reads reflect deletion.

- Scenario: Existing provider change event remains emitted
Result: PASS
Evidence: event emission remains in `packages/cli/src/api/providers.ts:416-420` and `455-459`.

- Scenario: Failed refresh preserves last good snapshot
Result: PASS
Evidence: `ProviderStateService.rebuildSnapshots()` builds local `providers` and `modelGroups` arrays and only assigns snapshots after a full successful rebuild at `packages/cli/src/services/provider-state-service.ts:131-132`, so a thrown error does not partially replace previous snapshots.

## Verification

- `npm run build`: PASS
- `npm test -- provider-api-cache.test.ts providers.test.ts`: FAIL
  - failing assertions in `packages/cli/tests/providers.test.ts:79` and `114-119`

## Overall

- Overall result: FAIL

<promise>FAIL</promise>
