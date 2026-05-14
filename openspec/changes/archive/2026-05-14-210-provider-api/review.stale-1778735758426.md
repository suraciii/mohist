## Findings

1. Error: full test suite still fails because legacy provider route tests assert the removed `models` field on `GET /api/providers`.
File: `packages/cli/tests/providers.test.ts:79`, `packages/cli/tests/providers.test.ts:114`
Evidence: `npm test` fails with:
`expected { id: '302ai', name: '302.AI', … } to have property "models"`
and
`expected Array.isArray(anthropicProvider.models) to be true`
Impact: the change does not meet the review requirement that new code has tests and all tests pass, and the repo still contains tests enforcing the old API contract.
Suggested fix: update `packages/cli/tests/providers.test.ts` so `GET /api/providers` asserts provider metadata only and moves model-shape assertions to `GET /api/providers/models`.

2. Warning: `ProviderStateService.refresh()` is not concurrency-safe, so overlapping successful writes can leave the cache with an older snapshot than the config file.
File: `packages/cli/src/services/provider-state-service.ts:48-50`, `packages/cli/src/services/provider-state-service.ts:60-133`
Evidence: every `refresh()` directly calls `rebuildSnapshots()`, and `rebuildSnapshots()` performs async work before assigning `this.providersSnapshot` / `this.modelsSnapshot` at the end. If refresh A starts, refresh B starts later and finishes first, refresh A can still finish afterward and overwrite B's newer snapshot.
Impact: subsequent `GET /api/providers` and `GET /api/providers/models` can serve stale data after concurrent config mutations.
Suggested fix: serialize refreshes or gate snapshot publication with a monotonic generation token so only the newest completed rebuild may replace the current snapshots; add a regression test covering overlapping writes.

## Spec Compliance

### http-api/spec.md

- PASS `provider-api-cached-reads` / Provider state prewarmed before serving requests
Evidence: `packages/cli/src/server/index.ts:264-265` constructs `ProviderStateService` and awaits `providerState.warm()` before routers are registered and before `server.start()` at `packages/cli/src/server/index.ts:317`.

- PASS `provider-api-cached-reads` / Provider list omits model IDs
Evidence: cached provider item shape in `packages/cli/src/services/provider-state-service.ts:5-14` has no `models` field; route returns `providerState.getProviders()` in `packages/cli/src/api/providers.ts:62-67`; regression test asserts omission in `packages/cli/tests/provider-api-cache.test.ts:89-100`.

- PASS `provider-api-cached-reads` / Provider models preserve selectable model response shape
Evidence: cached model group shape is defined in `packages/cli/src/services/provider-state-service.ts:16-26`; route returns `providerState.getProviderModelGroups()` in `packages/cli/src/api/providers.ts:121-126`; regression tests cover group and model fields in `packages/cli/tests/provider-api-cache.test.ts:127-190`.

- PASS `provider-list-omits-models` / Provider list UI consumes lightweight providers
Evidence: web `Provider` type no longer includes `models` in `packages/cli/web/src/lib/provider-api.ts:3-12`; settings provider cards only read metadata fields in `packages/cli/web/src/components/AiSettingsSection.tsx:82-153`.

- PASS `provider-list-omits-models` / Model selectors consume model groups endpoint
Evidence: `useModels()` reads `api.getAvailableModels()` in `packages/cli/web/src/hooks/useModels.ts:4-8`; API client maps that to `GET /providers/models` in `packages/cli/web/src/lib/api.ts:191`; model selectors read `modelProviders` and iterate `provider.models` from that endpoint in `packages/cli/web/src/components/AiSettingsSection.tsx:203-213`.

- FAIL `provider-api-performance-contract`
Evidence: new regression coverage exists in `packages/cli/tests/provider-api-cache.test.ts`, but `npm test` still fails because `packages/cli/tests/providers.test.ts:79` and `:114` still assert the removed `models` field on `GET /api/providers`.
Deviation: the repository does not currently satisfy the required regression-test state of passing tests for the new contract.

### provider-config/spec.md

- PASS `provider-state-refresh-after-config-change` / Provider save refreshes provider state
Evidence: route writes config, emits event, then awaits `providerState.refresh()` before returning success in `packages/cli/src/api/providers.ts:402-430`; test coverage in `packages/cli/tests/provider-api-cache.test.ts:201-219`.

- PASS `provider-state-refresh-after-config-change` / Custom provider model update refreshes model groups
Evidence: same refresh path in `packages/cli/src/api/providers.ts:422-424`; test coverage in `packages/cli/tests/provider-api-cache.test.ts:221-238`.

- PASS `provider-state-refresh-after-config-change` / Provider delete refreshes provider state
Evidence: delete handler awaits `providerState.refresh()` before success in `packages/cli/src/api/providers.ts:451-469`; tests cover providers and model groups in `packages/cli/tests/provider-api-cache.test.ts:240-284`.

- PASS `provider-state-refresh-after-config-change` / Existing provider change event remains emitted
Evidence: save handler emits at `packages/cli/src/api/providers.ts:416-420`; delete handler emits at `packages/cli/src/api/providers.ts:455-458`.

- PASS `provider-state-refresh-after-config-change` / Failed refresh preserves last good snapshot
Evidence: `rebuildSnapshots()` builds local `providers` / `modelGroups` arrays and only publishes them at `packages/cli/src/services/provider-state-service.ts:131-132`, so a thrown error before publication leaves the prior snapshot intact.

## Verdict

- Correctness: warning due to concurrent refresh stale-overwrite risk.
- Complexity: acceptable; the new service and route changes remain straightforward.
- Test Coverage: fail, because `npm test` still fails on outdated provider route tests.
- Security: no new secret exposure or injection issue found in reviewed changes.

<promise>FAIL</promise>
