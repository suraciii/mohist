## Context

mohist has **two configuration systems** that serve different purposes:

1. **`~/.mohist/config.jsonc`** (file-based, `config-loader.ts` + `config-schema.ts`): Stores provider credentials, model selection, opencode paths. Read at startup, cached in memory. Used by providers, model resolution, and server startup config.

2. **SQLite `config` table** (`config-repo.ts` + `config-service.ts`): Stores runtime tunables like `agent.timeout`, `poll.interval`. Mutable via `PUT /api/config/:key`. Used by the running server process.

Currently timeout values are hardcoded in three places:
- `ralph-executor.ts:431-432` — `DEFAULT_TASK_TIMEOUT_MS = 30min`, `MIN_TASK_TIMEOUT_MS = 10min`
- `workflow-loader.ts:32-68` — stage-specific timeouts (300s–7200s) in `DEFAULT_WORKFLOW`
- `acp-session.ts:66-67` — `DEFAULT_TIMEOUT = 30min`, `PER_ROUND_TIMEOUT = 30min`

The existing `agent.timeout` in SQLite stores a single value (30min in ms). The `config.jsonc` schema also has `agent.timeout` but it's not consumed by ralph-executor or workflow-loader today.

## Goals / Non-Goals

**Goals:**
- Add `taskTimeout`, `stageTimeout`, `maxGracePeriods` to `config.jsonc` schema under `agent`
- Provide runtime accessor functions that resolve config → defaults
- Replace hardcoded constants in `ralph-executor.ts` and `workflow-loader.ts`
- Validate values on write via API and schema parsing
- Config changes take effect on next issue start (no server restart)

**Non-Goals:**
- Unifying the two config systems (out of scope)
- Adding a CLI command for config management (use existing API)
- Per-stage or per-task-type timeout overrides (future enhancement)
- Changing `acp-session.ts` DEFAULT_TIMEOUT (it's already fed from `stageTimeoutMs`)

## Decisions

### D1: Store timeout config in `config.jsonc`, not SQLite

The new timeout keys (`agent.taskTimeout`, `agent.stageTimeout`, `agent.maxGracePeriods`) will be added to `config.jsonc` schema, read via `config-loader.ts`.

**Rationale:** These are user-facing settings that should persist across server restarts and be editable in a file. The SQLite `config` table is a runtime override mechanism — adding more keys there increases drift between file and DB state. The `config.jsonc` approach is consistent with how `agent.timeout`, `server.port`, and `log.level` are already handled.

**Consumption pattern:** Add accessor functions in `config-loader.ts` (like `getServerConfig`, `getLogConfig`) that return a typed object with defaults applied.

**Alternatives considered:**
- SQLite `config` table: Already has `agent.timeout` but values are strings, no type safety, and the API surface is generic key-value. Would require parallel validation logic.
- Separate `timeout.yaml`: Adds a third config source. Unnecessary complexity.

### D2: Config accessed via `load()` call, not injected dependency

`ralph-executor.ts` and `workflow-loader.ts` will call `load()` + `getAgentTimeoutConfig()` at the point of use (inside function bodies), not via constructor injection.

**Rationale:** Both files are currently stateless functions / use static defaults. Injecting a config service would require threading dependencies through `workflow-controller` → `ralph-executor` and changing the `DEFAULT_WORKFLOW` constant into a function. Calling `load()` directly is simpler and already used in `workflow-controller.ts:693` for `getBuildStageTimeoutMs()`.

**Cache invalidation:** `config-loader.ts` already has `clearConfigCache()` called by `writeConfig()`. Since config is only read at issue-start time (not mid-execution), the existing cache + invalidation is sufficient.

**Alternatives considered:**
- Dependency injection through `RalphExecutorContext`: More testable but over-engineered for a config read that happens once per issue.
- Global singleton: Anti-pattern, makes testing harder.

### D3: Default values chosen to match current hardcoded behavior

| Key | Default | Maps to current |
|-----|---------|-----------------|
| `taskTimeout` | 600s | `ralph-executor.ts` DEFAULT_TASK_TIMEOUT_MS (30min) was derived from stage budget; 600s is the `workflow-loader.ts` explore/plan timeout |
| `stageTimeout` | 3600s | `workflow-loader.ts` build stage 7200s / 2 tasks ≈ 3600s per task; overall stage budget |
| `maxGracePeriods` | 2 | Not currently configurable, hardcoded as 2 |

**Rationale:** The most common failure mode is task timeout. 600s (10min) gives agents enough time for most coding tasks while preventing runaway sessions. The stage timeout of 3600s (1hr) covers multi-task builds. These are conservative defaults that match what users experience today.

### D4: Validation in zod schema, not just API layer

Add `.refine()` or `.superRefine()` to the `agent` object in `ConfigInfoSchema` for range validation. This ensures invalid values are caught at parse time (file load) and at API write time.

### D5: Workflow YAML timeout takes precedence over config

`workflow.yaml` stage-level `timeout` field is the highest priority override. The resolution order is:
1. `workflow.yaml` explicit `timeout` → use as-is
2. `config.jsonc` `agent.stageTimeout` → use as default
3. Hardcoded fallback → 600s (safety net)

**Rationale:** Project-specific workflows should be able to override global config. This is already the pattern — `workflow.yaml` already has per-stage timeouts that override `DEFAULT_WORKFLOW`.

## Risks / Trade-offs

- **[Two config sources may confuse users]** → Mitigation: `config.jsonc` is the documented primary config path. SQLite overrides are an internal detail exposed only via API. The new keys only live in `config.jsonc`.
- **[Config cache may serve stale values if file is edited externally]** → Mitigation: `load()` caches per file path; `clearConfigCache()` is called on writes. External edits require server restart or API write to invalidate — acceptable trade-off.
- **[MIN_TASK_TIMEOUT_MS lowered from 10min to 60s may cause more timeout failures on slow tasks]** → Mitigation: The minimum is only the floor when `stageTimeoutMs / taskCount` is very low. Users can raise `stageTimeout` to prevent this. The default 600s per task is well above the floor.

## Migration Plan

1. Add new fields to `config-schema.ts` with `.optional()` — backward compatible
2. Add `getAgentTimeoutConfig()` to `config-loader.ts`
3. Update `ralph-executor.ts` to use config values
4. Update `workflow-loader.ts` to accept optional config for defaults
5. Update `config-service.ts` to expose the new keys in `getConfig()` response and add validation
6. No DB migration needed — SQLite `config` table is key-value, new keys are added on demand

**Rollback:** Remove the new schema fields. Hardcoded defaults are preserved as fallback values in the accessor functions.

## Open Questions

None — the design is straightforward and builds on existing patterns.
