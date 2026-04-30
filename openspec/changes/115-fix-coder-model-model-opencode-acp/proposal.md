## Why

The Coder Model selector in the Issue Detail and Settings pages displays the wrong model list (mohist AI SDK models instead of opencode ACP-compatible models), and critically, the `model` parameter is never forwarded through ACP to opencode — making any model selection completely non-functional. This breaks the per-issue model override feature (Issue #80) and stage-aware model routing (Issue #74) that were designed to let users control which LLM opencode uses.

## What Changes

### High Priority (Must Fix)

- **Discovery Service data structure**: Fix `GET /api/opencode/models` response parsing — opencode returns `AgentModel[]` objects, not `Model[]`. The API currently parses them as `Model[]` causing empty/wrong lists.

- **Frontend Coder Model dropdown data source**: Switch IssueDetail's Coder Model selector and Settings' Default Coder Model selector from `useMohistModels` to `useOpencodeModels` hook.

- **Backend `createAcpConnection` model parameter**: `createAcpConnection(options)` receives `model` but never destructures or uses it. After `connection.newSession()` succeeds, call `connection.setSessionConfigOption({ scope: 'agent', option: 'model', value: model })`.

- **Backend `runAcpSession` model parameter**: Same fix — after `connection.newSession()`, call `setSessionConfigOption` with the model.

- **Backend `runAcpSession` oneshot calls** (e.g., explore/fix-build prompts): These also have a `model` parameter that is currently ignored. Apply the same fix.

### Medium Priority

- **Model priority resolution**: `opencode.model` config is hardcoded in multiple places. Implement a clear priority chain: `issue.model > stageModels[stage] > opencode.model > default`. The frontend IssueModelSelector and Settings already store these values, but the backend never uses them.

### Breaking Changes

- None — all changes are backward-compatible bug fixes.

## Capabilities

### New Capabilities

- `opencode-model-passing` — Model selection is forwarded through ACP to opencode, enabling actual model switching (e.g., `anthropic/claude-3-5-sonnet`, `gpt-4o`) instead of being silently ignored.

### Modified Capabilities

- `coder-session-tracking` — The `coder_session_started` event will now carry the `model` field, allowing frontend and logs to show which model was actually used. The existing requirement for `rawInput`/`rawOutput`/`title` enrichment remains unchanged.

## Impact

**Affected code:**
- `packages/cli/src/services/discovery.ts` — Fix `AgentModel[]` vs `Model[]` parsing
- `packages/cli/src/agent/acp-session.ts` — `createAcpConnection` and `runAcpSession` must forward `model` via `setSessionConfigOption`
- `packages/cli/src/workflow/workflow-controller.ts` — Pass `model` when calling `runAcpSession`
- `packages/cli/src/api/routes.ts` or related — `useOpencodeModels` hook must be wired to IssueDetail CoderModel selector and Settings DefaultCoderModel
- `packages/cli/src/services/models-service.ts` — May need adaptation for `AgentModel[]` → API compatibility

**Affected specs:**
- `spawn-coder/spec.md` — ACP `session/new` + `setSessionConfigOption` flow not documented; add clarification
- `coder-session-tracking/spec.md` — Add model field to `coder_session_started` event

**No impact on:**
- `pipeline-model` (workflow stages)
- `http-api` (existing endpoints unchanged, just the `/api/opencode/models` response parsing internally)
