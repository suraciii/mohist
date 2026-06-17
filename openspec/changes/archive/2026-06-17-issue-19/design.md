## Context

Settings > System and Settings > Runtime are partially broken on the .NET server because the Web settings client still calls `/api/log-level` and `/api/agent-runtime`, both of which return 404. The server already exposes equivalent durable configuration through `/api/config` and `PUT /api/config/{key}` for `logLevel`, `maxConcurrentAgents`, `agentTimeout`, `taskTimeout`, `stageTimeout`, `pollInterval`, and `maxGracePeriods`.

`/api/system/info` is already implemented and provides truthful System diagnostics for running/source/install/update/services/paths. The remaining System issue is log-level truthfulness and save feedback. The Runtime issue is broader: the page fails as a whole instead of deriving its UI model from supported configuration and disabling unsupported controls.

Stakeholders are Web UI users configuring Mohist, .NET server/API maintainers, and test owners for settings regressions. The main constraint is to restore existing supported behavior without widening the scope into runtime liveness, project-scoped repository settings, model-selection bugs, or agent model source-of-truth decisions.

## Goals / Non-Goals

**Goals:**
- Make Settings > System Log Level read the persisted value and persist `DEBUG`, `INFO`, `WARN`, and `ERROR` through a supported backend contract.
- Make Settings > Runtime load on the .NET server using implemented configuration APIs instead of requiring `/api/agent-runtime`.
- Preserve a stable UI-facing runtime model while mapping to server config keys: `agentTimeout`, `maxConcurrentAgents`, `taskTimeout`, `stageTimeout`, `pollInterval`, and `maxGracePeriods`.
- Show visible failures for load/save errors and disable or explain unsupported fields instead of silently accepting or failing entire panels.
- Add focused backend and Web regression tests around the restored settings contracts.

**Non-Goals:**
- Do not change runtime timeout semantics beyond exposing and editing existing configuration values.
- Do not implement runner liveness/status lists or stale-runtime readiness checks.
- Do not expose secrets or environment variables.
- Do not solve Coder Agent model popover behavior or the global/project model source-of-truth decision.
- Do not introduce broad settings-page UX cleanup outside the broken System Log Level and Runtime paths.

## Decisions

### Decision: Use `/api/config` as the canonical settings contract

Migrate the Web settings API layer so log-level reads come from `GET /api/config.logLevel` and writes use `PUT /api/config/logLevel`. Runtime reads shall derive an `AgentRuntimeConfig` UI model from `GET /api/config`; runtime writes shall fan out changed fields to `PUT /api/config/{key}` for each supported key and then refetch config.

Rationale: `/api/config` and `PUT /api/config/{key}` already exist and are verified for the keys required by this issue. This fixes the broken UI with the smallest server surface area and avoids adding duplicate compatibility endpoints.

Alternatives considered:
- Add `/api/log-level` and `/api/agent-runtime` to the .NET server. This preserves the old Web client shape but creates duplicate settings contracts that must stay in sync.
- Add a new consolidated `/api/settings/runtime` endpoint. This gives a cleaner future API but is more scope than needed when equivalent config keys already exist.

### Decision: Keep the runtime adapter in the Web settings entity layer

Implement the conversion between server config keys and `AgentRuntimeConfig` in `packages/web/src/entities/settings/api/client.ts` or adjacent settings API code, not inside the `AgentSettingsSection` component. The page should continue consuming `useAgentRuntime()` and `useSetAgentRuntime()` as a stable UI-facing contract.

Rationale: Keeping adaptation in the entity API layer localizes backend contract knowledge and keeps the settings component focused on form state, validation, and display. It also makes tests easier because the component can still mock the runtime settings hook shape.

Alternatives considered:
- Refactor the component to call `useConfig()` directly. This reduces one abstraction but leaks config-key naming and unit conversion concerns into the UI.
- Create a new backend-specific settings store. This adds unnecessary indirection for one page and one existing API contract.

### Decision: Preserve UI units while normalizing server units explicitly

The Runtime form shall continue showing minutes/seconds/counts, while the adapter maps server values to the existing `AgentRuntimeConfig` units expected by the form. `agentTimeout`, `taskTimeout`, and `stageTimeout` are seconds in server config and must be converted to milliseconds if the existing UI model remains millisecond-based; `pollInterval` is already stored in milliseconds and maps directly to the UI model before the existing seconds display conversion.

Rationale: The current component converts `AgentRuntimeConfig` milliseconds into form minutes/seconds. A narrow adapter avoids changing form behavior and minimizes UI churn.

Alternatives considered:
- Change `AgentRuntimeConfig` to store server-native seconds. This removes conversion in the adapter but requires broader component and test updates.
- Store all values as strings to match raw config responses. This weakens type safety and complicates validation.

### Decision: Treat unsupported fields as metadata-driven disabled controls when needed

The runtime adapter should classify each field as supported if it is present in config or known to be persistable through `/api/config/{key}`. Unsupported fields should stay visible only when useful for user context, disabled with explanatory copy, and excluded from save/reset payloads.

Rationale: Issue 19 requires partial availability to be explicit without failing the whole panel. The current required fields are supported, but this approach prevents future missing fields from regressing into misleading defaults.

Alternatives considered:
- Hide unsupported fields entirely. This avoids disabled states but can make missing functionality look accidental.
- Allow editing unsupported fields and rely on backend rejection. This recreates the silent/misleading failure class the issue is fixing.

### Decision: Validate log level in the server config path

Ensure the .NET config update path rejects unsupported `logLevel` values and persists only `DEBUG`, `INFO`, `WARN`, and `ERROR`. Add tests for read, successful update, and invalid update behavior.

Rationale: The Web UI must not be the only enforcement point. Server-side validation protects all clients using `/api/config/logLevel`.

Alternatives considered:
- Validate only in the Web UI. This is insufficient for API correctness and does not satisfy backend test expectations.
- Accept arbitrary strings and rely on logging infrastructure behavior. This risks invalid persisted state and inconsistent UI display.

## Risks / Trade-offs

- [Risk] Multiple runtime field writes can partially succeed if one `PUT /api/config/{key}` fails -> Mitigation: send only changed supported fields, surface the failing error, invalidate/refetch config after mutation, and keep UI state tied to last confirmed backend state.
- [Risk] Unit mismatch between server config and UI runtime model can produce wrong timeout values -> Mitigation: centralize conversion in the settings API adapter and cover representative values in Web tests.
- [Risk] Existing defaults in `AgentSettingsSection` may differ from server defaults -> Mitigation: prefer loaded `/api/config` values and use client defaults only for initial skeleton/form fallback before data arrives or for explicit reset values aligned with server defaults.
- [Risk] Removing calls to `/api/agent-runtime` could miss runtime-only metadata not present in config -> Mitigation: keep issue 19 scoped to scheduling values explicitly available from config and use `/api/opencode/runtime` only for coder runtime mode/model display if needed.
- [Risk] Config schema accepts `logLevel` as a generic string today -> Mitigation: add targeted validation for log-level enum values in the config service or route layer.

## Migration Plan

1. Update backend config validation so `logLevel` accepts only `DEBUG`, `INFO`, `WARN`, and `ERROR`, and verify existing `/api/config` responses include the supported runtime keys.
2. Replace `getLogLevel`/`setLogLevel` implementation in the Web settings API layer to use `/api/config` and `/api/config/logLevel`, returning the existing `{ level }` UI shape.
3. Replace `getAgentRuntime`/`updateAgentRuntime` implementation to derive and persist the existing `AgentRuntimeConfig` shape through `/api/config` and `PUT /api/config/{key}`.
4. Update Runtime UI behavior as needed to disable unsupported fields, avoid misleading defaults after load failures, and preserve visible save errors.
5. Add backend tests for config read/write validation and Web tests for System log-level load/save failure plus Runtime load/save/unsupported-field states.
6. Rollback strategy: revert the Web adapter changes to the old endpoint calls and revert any validation changes. Since no persisted data migration is introduced, rollback does not require data transformation.

## Open Questions

- Should reset write explicit server default values through `/api/config/{key}` or clear keys to let `ConfigService` defaults apply? Prefer explicit default writes unless the config API already exposes clear semantics to the Web client.
- Should `pollInterval` remain editable on the Runtime page even though the issue acceptance criteria focuses on concurrency, session timeout, task timeout, stage timeout, and grace periods? Current UI exposes it and the backend supports it, so the design keeps it supported unless product decides to disable it.
- Should the Web UI show a dedicated unsupported-field badge or plain helper text? Either satisfies the spec; use the existing settings visual language when implementing.
