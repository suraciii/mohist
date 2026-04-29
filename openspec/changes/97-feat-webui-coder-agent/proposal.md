## Why

Per-issue model override is silently overwritten by the global `opencode.model` config due to a priority bug in `acp-session.ts`, and users have no way to view or change the default coder model through the WebUI — they can only edit `~/.mohist/config.jsonc` by hand. This makes the model selection experience broken and opaque.

## What Changes

- **Fix model priority bug**: In `acp-session.ts`, when `model` (per-issue override) is already set, skip the `configuredModel` `setSessionConfigOption` call. Per-issue override must take precedence over `config.opencode.model`.
- **Add `GET /api/opencode-config/model`**: Read `config.opencode.model` from config.jsonc and return it.
- **Add `PUT /api/opencode-config/model`**: Write `config.opencode.model` to config.jsonc via `load()` → modify → `writeConfig()`. Accept `null` to clear the value.
- **Add "Default Coder Model" field in Settings > General**: A model selector dropdown (reusing IssueModelSelector UI pattern) that reads from and writes to the new API. Model list sourced from `GET /opencode/models`. Supports clearing the value to restore opencode's internal default.
- **Update IssueModelSelector "Use default" label**: Change from "Use default" to "Use default (model-name)" by fetching the current default coder model from the new API.

## Capabilities

### New Capabilities

- `opencode-model-config-api`: REST API endpoints for reading/writing the default coder model (`config.opencode.model`).
- `default-coder-model-setting`: WebUI Settings > General section for configuring the default coder model with model selector dropdown.

### Modified Capabilities

- `spawn-coder`: Model priority logic fix — per-issue model override must not be overwritten by config model.
- `web-ui`: IssueModelSelector "Use default" label now shows the actual default model name.

## Impact

- **`packages/cli/src/agent-runtime/acp-session.ts`**: Fix model priority bug (lines ~1124-1175 in the `createMultiRoundAcpSession` function; similar logic exists in the single-round path around line ~600).
- **`packages/cli/src/api/`**: New REST API routes for opencode-model config.
- **`packages/cli/src/services/`**: New service or extension of existing config service for opencode-model read/write via config.jsonc.
- **`packages/cli/src/server/`**: Route registration.
- **Frontend Settings page**: New "Default Coder Model" section in GeneralSettingsSection.
- **Frontend IssueModelSelector**: Label update to display default model name.
- **`~/.mohist/config.jsonc`**: Existing `opencode.model` field — no schema change, just now exposed via API.
