## Context

`acp-session.ts` 中两个 session 创建路径（single-round ~L605, multi-round ~L1124）都存在相同的优先级 bug：先通过 `setSessionConfigOption` 设置 per-issue `model`，然后无条件地再次调用 `setSessionConfigOption` 设置 `config.opencode.model`，导致 per-issue override 被静默覆盖。

配置读写已有成熟的 `config-loader` 模式：`load()` → 修改 → `writeConfig()`（带乐观锁），providers API (`api/providers.ts`) 就是这个模式。前端 `GeneralSettingsSection` 已有数值型配置字段，使用 `useConfig` / `useUpdateConfig` hooks 和 `/api/config` SQLite 路由。前端模型选择器 (`IssueModelSelector`) 已有完整的下拉搜索 UI，但使用的是 `/providers/models`（provider 注册表模型），而 opencode 实际可用模型应来自 `/opencode/models`。

## Goals / Non-Goals

**Goals:**
- Fix model priority bug so per-issue override always wins over config default
- Expose `config.opencode.model` read/write via REST API using config-loader pattern
- Add "Default Coder Model" selector to Settings > General with opencode model list
- Show actual default model name in IssueModelSelector's "Use default" label

**Non-Goals:**
- `opencode.stageModels` WebUI configuration
- `config.model` (Explore agent default) WebUI configuration
- Unifying IssueModelSelector's model list source to opencode models (it stays on `/providers/models` for now)
- Refactoring IssueModelSelector into a shared component (just inline the simpler variant in GeneralSettingsSection)

## Decisions

### D1: API endpoint path uses `/api/opencode-config/model`

Dedicated endpoint under a new `/api/opencode-config` prefix. The existing `/api/config` prefix already has a `PUT /:key` catch-all route (SQLite-backed) that would intercept `/api/config/opencode-model` requests, so a separate prefix avoids route collision.

**Alternatives considered:**
- `/api/config/opencode-model` — conflicts with existing `PUT /api/config/:key` catch-all in `api/config.ts` (Hono matches parameterized routes from earlier-registered routers first)
- Extending existing `/api/config/:key` route with a special case — couples two storage backends
- `PUT /api/config/opencode.model` (dot in path) — works but URL-unfriendly

### D2: New API route file `api/opencode-model-config.ts` registered at `/api/opencode-config`

Follows the pattern of `api/providers.ts` — standalone Hono router that uses `load()` / `writeConfig()` directly, with `ConfigConflictError` → 409 handling. Registered at `/api/opencode-config` to avoid collision with the existing `PUT /:key` catch-all in the `/api/config` router.

**Alternatives considered:**
- Adding to `api/config.ts` — that file is SQLite-backed; mixing concerns. Also the `/:key` catch-all would conflict.
- Adding to `api/providers.ts` — semantically different (not provider-related)

### D3: Settings selector inlines a simplified model dropdown

Rather than extracting a shared component from `IssueModelSelector`, inline a simpler Popover-based selector directly in `GeneralSettingsSection`. The IssueModelSelector has per-issue concerns (recent models, issue API calls, provider grouping) that don't apply to the settings context. The settings version uses `string[]` from `/opencode/models` and calls `/api/config/opencode-model`.

**Alternatives considered:**
- Extract shared `ModelSelectorBase` component — over-engineering for this scope; YAGNI until third consumer
- Reuse `IssueModelSelector` directly — different data source, different write target, different UX (no recent models, no provider grouping)

### D4: IssueModelSelector fetches default model name via direct API call

Add a `useQuery` for `GET /api/config/opencode-model` inside `IssueModelSelector`, used only to format the "Use default" label. This keeps the change localized — no context provider or global state needed.

**Alternatives considered:**
- React Context for default model — over-engineering, only one consumer
- Include in existing `useConfig` response — mixes SQLite config and file config in one query

### D5: Model display name uses last segment after `/`

Both `IssueModelSelector` and the new settings selector display `"claude-sonnet-4"` for `"anthropic/claude-sonnet-4"`. This is consistent with the existing `currentModel.split('/').pop()` pattern in IssueModelSelector.

## Risks / Trade-offs

- [Model list staleness] `/opencode/models` depends on opencode server being reachable — if it's down, the settings selector shows empty model list. → Mitigation: show "No models available" message; existing model name still displayed from config read.
- [Config write race] Two browser tabs writing config simultaneously could conflict. → Mitigation: optimistic locking via `_version` field already in `writeConfig()`; returns 409 on conflict.
- [Two model list sources] IssueModelSelector still uses `/providers/models` while settings uses `/opencode/models` — lists may differ. → Accepted: this is a known gap deferred to a future iteration.

## Migration Plan

No migration needed. The change is additive:
1. Deploy backend: new API route + priority fix
2. Deploy frontend: settings field + IssueModelSelector label update
3. Existing `config.jsonc` with `opencode.model` works unchanged — the field was already read by `acp-session.ts`, just not exposed via API

Rollback: remove the route registration and revert the frontend. No DB changes involved.

## Open Questions

None — all decisions resolved in proposal/spec phase.
