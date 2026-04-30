# Review Report

## Result: FAIL

## Dimensions

### Correctness: PASS

- Backend API endpoints (`settings-config.ts`) correctly validate inputs, use atomic operations for agent-runtime batch updates, and handle edge cases (null model, empty config sections cleanup).
- Frontend components correctly manage local state, dirty tracking, and optimistic mutations.
- All TypeScript types are correct — `AgentRuntimeConfig`, `SystemInfo` properly defined in `types.ts` and aligned between frontend and backend.
- Build passes with zero errors.
- No off-by-one errors or logic bugs detected.

### Complexity: PASS

- `settings-config.ts` is 364 lines with 11 endpoint handlers — each handler is under 40 lines, well-structured with extracted validators and helper functions.
- `AiSettingsSection.tsx` (630 lines) is the largest component but is well-decomposed with sub-components (`ConnectedProviderCard`, `AvailableProviderCard`, `CustomProviderCard`, `ModelSelect`, `TimeoutDiagram`).
- `AgentSettingsSection.tsx` (483 lines) has clean separation of concerns — form state management, validation, and rendering are well-organized.
- No excessive cyclomatic complexity. No copy-pasted code.

### Test Coverage: PASS (with warnings)

- No new tests were added for the new backend API endpoints (`settings-config.ts`).
- No new tests were added for the frontend section components.
- The 17 failing test files (`acp-hang-recovery`, `agent-runner-service`, `e2e`, `merge-queue`, etc.) are all **pre-existing failures** unrelated to this change — they test infrastructure (worktree rebase, ACP sessions, database) and fail on the base commit as well.
- **Warning**: The new API routes and React components have no test coverage. This is a risk for regression but does not constitute broken tests.

### Security: PASS

- Input validation is thorough: model format validated via regex (`/^[^/]+\/.+$/`), log levels validated against whitelist, agent runtime fields validated with range checks.
- `getGitHash()` uses `execSync` with `stdio: ['pipe', 'pipe', 'pipe']` and `timeout: 3000` — no injection risk since the command is static.
- `getVersion()` reads from package.json via `path.join(__dirname, ...)` — no user-controlled path traversal.
- API key masking in frontend (`maskApiKey` in `useQueries.ts:327`) correctly masks keys.
- No secrets exposed in API responses.

### Spec Compliance: FAIL

#### T-001: Config Schema Extension — PASS
- `config-schema.ts:24-31` correctly adds `stageTimeout`, `taskTimeout`, `maxGracePeriods`, `pollInterval` as optional number fields within `agent` object.
- Backward compatible — all fields are optional.

#### T-002: Backend API Endpoints — FAIL (1 issue)
- **PASS**: All 11 endpoints implemented and registered in `server/index.ts:260`.
- **PASS**: `GET /api/system/info` returns version, gitHash, server, paths.
- **PASS**: Model endpoints validate format with regex, return 400 for invalid.
- **PASS**: Agent-runtime validates atomically — no partial updates on validation failure.
- **PASS**: Stage-models validates model format per stage.
- **FAIL**: `PUT /api/config/log-level` does NOT update the runtime logger level immediately. The spec requires "运行时 logger 级别立即生效（不重启）" but the implementation only writes to `config.jsonc` via `writeConfig()`. The `Log` namespace in `packages/cli/src/util/log.ts` has a module-scoped `level` variable (line 16) that is only set during `Log.init()` (line 74) and has no exported `setLevel` function. The runtime logger will continue using the old level until server restart.

#### T-003: Frontend Hooks and API Client — PASS
- All API methods present in `api.ts`: `getModel`, `setModel`, `getOpencodeModel`, `setOpencodeModel`, `getLogLevel`, `setLogLevel`, `getAgentRuntime`, `updateAgentRuntime`, `getStageModels`, `setStageModels`, `getSystemInfo`.
- All hooks present in `useQueries.ts` with correct query keys and invalidation logic.

#### T-004: Settings Routing + Sidebar Layout — PASS
- `App.tsx:99`: `/settings` redirects to `/settings/ai` via `<Navigate>`.
- `App.tsx:100`: `/settings/:section` renders `<SettingsPage />`.
- `SettingsPage.tsx:59-61`: Invalid section redirects to `/settings/ai`.
- Sidebar is sticky (`sticky top-6`, line 86), content area scrolls independently.
- Mobile: `md:hidden` dropdown (line 70), `hidden md:flex` sidebar (line 84).

#### T-005: AI Settings Section — PASS
- Unified provider list: connected providers sorted first (line 357-359), unconfigured after.
- Connected providers show `●` (line 102), masked key (line 114), source tag (line 112), Remove button.
- Unconfigured providers show `○` (line 132), description (line 136-138), Connect button.
- Search filter by name/id (lines 361-367).
- Custom Providers sub-section with independent Add button (lines 493-519).
- Mohist Model selector with model list from `useModels` (lines 528-538).
- Coder Model selector with Clear button (lines 541-554), placeholder "Same as Mohist Model".
- Stage Model Overrides collapsible panel (lines 560-588), default collapsed, "Advanced" label.

#### T-006: Agent Settings Section — PASS
- Three timeout inputs with minutes unit (lines 356-378).
- ASCII diagram dynamically reflects input values (lines 148-164).
- Max Concurrent with 1-16 range validation (lines 67-72).
- Poll Interval with >= 5 validation (lines 74-78).
- Retry Budget with >= 0 validation (lines 80-84).
- Section-level Save with dirty state tracking (lines 219-223).
- Save disabled when not dirty or validation errors (line 438).
- Save shows loading state "Saving..." (line 445).
- Reset to Defaults with confirmation dialog (lines 456-480).
- Save failure preserves user input (line 281 sets error but doesn't reset).

#### T-007: System Settings Section — PASS
- Log Level dropdown with DEBUG/INFO/WARN/ERROR (lines 97-108).
- Log Level change calls API, reverts on failure (lines 48-61).
- Log path shown as read-only `~/.mohist/logs/` (line 120).
- About section displays all required info from `GET /api/system/info` (lines 129-154).
- Server running shows green badge (lines 8-14), stopped shows gray (lines 16-21).
- Warning text "⚠ 修改服务器配置请编辑 config.jsonc 并重启" present (line 159).
- All About fields are read-only spans, no input elements.

#### T-008: Cleanup and Integration — PASS
- `GeneralSettingsSection.tsx` deleted.
- No references to `TabPanel`, `ConnectedProvidersList`, `AvailableProvidersList` in any source files.
- Header Settings link navigates to `/settings/ai` (Header.tsx:162).
- Build passes.

## Fix Suggestions

1. **`packages/cli/src/api/settings-config.ts:202-235`**: The `PUT /api/config/log-level` handler must also update the runtime Log level. Add a `Log.setLevel()` export to `packages/cli/src/util/log.ts` that updates the module-scoped `level` variable, then call it from the PUT handler after `writeConfig()`. For example:

   In `log.ts`, add:
   ```typescript
   export function setLevel(newLevel: Level) {
     level = newLevel
   }
   ```

   In `settings-config.ts`, after line 216 (`writeConfig(config)`):
   ```typescript
   import { Log } from '../util/log';
   // ...
   Log.setLevel(body.level as Log.Level);
   ```

   This satisfies the spec requirement "运行时 logger 级别立即生效（不重启）".
