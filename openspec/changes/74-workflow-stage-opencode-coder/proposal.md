## Why

Currently mohist spawns opencode acp coder sessions with no model override — the model is entirely determined by opencode's own defaults. This prevents users from optimizing cost (design stages need lightweight models, coding stages need strong ones) and from managing models centrally in `~/.mohist/config.jsonc`. Additionally, users have no way to discover which models their opencode environment actually supports.

## What Changes

- **Model discovery service** — a short-lived ACP probe extracts `availableModels` from `newSession` response, cached with 5-minute TTL; exposed via `GET /api/opencode/models`
- **Config schema extension** — `opencode.model` (global default) and `opencode.stageModels` (per-stage overrides: `plan`, `build`, `check`) added to `config.jsonc` schema
- **Stage-aware model selection** — `AcpSessionOptions` gains `stage?: string`; after `newSession`, `setSessionConfigOption({ configId: "model", value: "..." })` switches the model before `prompt`
- **Priority rule**: `stageModels[currentStage] > opencode.model > opencode built-in default`
- **Validation on spawn** — configured model is checked against discovered list; invalid config fails with a clear error listing available models
- **Fallback on failure** — if the configured model becomes unavailable at runtime (expired key, quota exceeded, provider error), the session automatically falls back to `opencode.model` or the opencode built-in default and logs a `model_fallback` event
- **Visibility** — `workflow_log` records `model_selected` and `model_fallback` events; viewable via `mo issue logs` and Web UI
- **Backward compatibility** — when `opencode.model` / `opencode.stageModels` are absent, behavior is identical to today

## Capabilities

### New Capabilities

- `opencode-model-discovery` — discover available opencode models via ACP probe; cached; exposed via REST API
- `stage-model-routing` — select opencode coder model per workflow stage via config; includes validation and runtime fallback

### Modified Capabilities

- `spawn-coder` — `AcpSessionOptions` interface extended with `stage?: string`; model override applied after `newSession` and before first `prompt`; spawn fails fast with helpful error on invalid model; runtime model failure triggers fallback and `model_fallback` logging
- `workflow-log` — new event types `model_selected` and `model_fallback` recorded and queryable via API

## Impact

- New service: `src/services/opencode-discovery-service.ts`
- Config schema: `config-schema.ts` gains `opencode.model` and `opencode.stageModels`
- ACP session: `acp-session.ts` — `newSession` → `setSessionConfigOption("model", ...)` chain; `AcpSessionOptions` interface updated
- API: `GET /api/opencode/models` endpoint added
- DB: `workflow_log` table gains two new `event_type` values
- No changes to existing pipeline stage flow or approval gates
