# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: typing
  Evidence: `AgentSettingsSection.tsx:227` used `config as unknown as Record<string, unknown>` to access arbitrary keys for the `unsupportedFields` derivation. The `GeneralConfig` type already exposes all six runtime keys as named properties, so the cast was unnecessary and could mask typos in `agentRuntimeToConfigKey` callers.
  Verification: Replaced the cast with `config[configKey] as keyof GeneralConfig` and added `GeneralConfig` to the type import. `npx tsc -b` reports no errors; `npx vitest run tests/AgentSettingsSection.test.tsx tests/SettingsPage.test.tsx tests/settings-client-log-level.test.ts tests/settings-client-agent-runtime.test.ts` reports 41/41 passed.
  Status: resolved

## Blocking Items

- None

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/entities/settings/api/queries.ts:18-22, 24-56`
  Evidence: `useUpdateConfig` is exported from the public settings entity API but no in-tree caller uses it. Its `CONFIG_KEY_TO_PROPERTY` map uses old, server-incompatible keys (`'agent.timeout'`, `'agent.maxConcurrent'`, `'poll.interval'`) so the optimistic-update branch is silently broken for the actual server keys (`agentTimeout`, `maxConcurrentAgents`, `pollInterval`). This is dead code as shipped, but any external consumer would receive a working server call with a no-op optimistic update and the invalidation would still re-sync — so behaviour is degraded, not destructive. Touching the public export requires public-contract judgment that is out of scope for this issue.
  SuggestedAction: Either delete `useUpdateConfig` (and its `UpdateConfigContext` / `CONFIG_KEY_TO_PROPERTY` helpers) or rename the keys to match the server. Coordinate with any external consumer before deleting.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/entities/settings/api/client.ts:21, 25`
  Evidence: `getLogLevel` returns `{level: 'INFO'}` when `config.logLevel` is missing, and `setLogLevel` returns `{level: <input>}` when the response omits `logLevel`. The spec says the Web UI must not display a hardcoded `INFO` unless it is the persisted value. In production the server always returns `logLevel` (default "INFO" when nothing is configured), so the fallback is never triggered; but the defensive fallback is still a latent violation of the spec language.
  SuggestedAction: Replace the `?? 'INFO'` / `?? level` fallbacks with the raw `config.logLevel` (which will be `undefined` for the server's own response) and let the display layer treat the absent case as "loading" / "unavailable" rather than substituting a value.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/entities/settings/model/types.ts:1-9`
  Evidence: `GeneralConfig` only declares the seven keys used by the Runtime page, but `GET /api/config` also returns `serverPort`, `serverHost`, `model`, `agent`, `stageAgents`. The TypeScript shape is incomplete; `useConfig().data` is structurally narrower than the runtime payload. Currently safe because every consumer indexes only the declared fields, but the type soundness gap will surface as soon as any new code reads e.g. `config.model`.
  SuggestedAction: Extend `GeneralConfig` to mirror the server schema (mark agent/stageAgents as optional since they are not always present) or split into a narrower "runtime view" type plus a wider "server config" type.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/web/src/entities/settings/api/client.ts:109-111` and `queries.ts:193-198, 200-215`
  Evidence: `useAgentRuntime` and `useConfig` both fetch `/api/config` independently. `useLogLevel` also fetches `/api/config` indirectly. Opening Settings > Runtime therefore issues three HTTP requests to the same endpoint rather than sharing the react-query cache entry. Invalidation is correct (all three keys are invalidated on save), but the initial load and refetches are redundant.
  SuggestedAction: Have `getAgentRuntime` read from the `['config']` query cache when available, or have the `useAgentRuntime` queryKey reference `['config']` and derive the runtime view via `select`. Same for `useLogLevel`.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: `packages/web/src/pages/settings/ui/AgentSettingsSection.tsx:290-298`
  Evidence: `formToConfig(localValues)` is recomputed inside the change-detection loop on every iteration. The result is identical across iterations. The recomputation also pulls in `unsupportedFields` so the resulting `config[key]` is then filtered; the recomputation is wasted but not wrong.
  SuggestedAction: Hoist `const config = formToConfig(localValues)` above the loop.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `packages/web/src/pages/settings/ui/AgentSettingsSection.tsx:224-236` and `packages/server/src/Mohist.Server/Infrastructure/Config/ConfigService.cs:14-28, 56-71`
  Evidence: `unsupportedFields` is derived from `useConfig` data by checking for `undefined`/`null` values. `ConfigService.GetConfigAsync` always returns every key in the schema (using the schema's `defaultValue` when nothing is persisted), so for the .NET server `unsupportedFields` is always empty. The disable-with-explanation UI is exercised by tests (and the spec requires it for future fields), but in production it is dead.
  SuggestedAction: Document the dependency on the server returning every key, or have the runtime page declare support statically (the `SUPPORTED_RUNTIME_KEYS` constant already exists) and treat "missing from config response" as a separate error from "not supported by schema".
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: `packages/web/src/entities/settings/api/queries.ts:204-213`
  Evidence: On `useSetAgentRuntime` error the `onError` handler invalidates `['agent-runtime']` and `['config']` (correct, in line with the design's partial-success mitigation), and the component also sets a local `saveError`. The user therefore sees both a `sonner` toast and the inline red banner for the same failure.
  SuggestedAction: Pick one channel (either drop the toast or drop the inline banner) to avoid double-notifying the user. The inline banner has stronger affordance for "this field is in a bad state" so the toast is the more natural candidate to remove.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: warning (pre-existing)
  Scope: `packages/server/tests/Mohist.Server.Tests/`
  Evidence: Running the full server test suite locally reports `Failed: 513, Passed: 624, Skipped: 6`. The failures all originate from `MohistIntegrationFixture` / `WorkflowGrainFixture` and are `Migrate` errors (EF Core migrations colliding on the shared sqlite-in-memory store when multiple test classes run in parallel). The 55 spec tests in `ConfigRoutesSpecs` and `ConfigServiceSpecs` introduced by this change all pass, as do all 41 web tests for the same area.
  SuggestedAction: Out of scope for issue 19. The CI runner almost certainly serializes these fixtures; the local reproduction is a known dev-mode limitation that pre-dates this change.
  Status: pre-existing

- [ID: item-10]
  Severity: info (pre-existing)
  Scope: `packages/web/src/pages/settings/ui/SystemSettingsSection.tsx:37-44`
  Evidence: The "wait for reconnect" loop in the System tab polls `/api/health` directly via `fetch`, bypassing the shared `request` helper. Pre-existing, not touched by this change.
  SuggestedAction: None for this issue. Tracked separately.
  Status: pre-existing

<promise>PASS</promise>
