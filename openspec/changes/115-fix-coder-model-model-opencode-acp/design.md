## Context

Three bugs converge to make the Coder Model selector completely non-functional:

1. **Discovery Service parses wrong structure**: `GET /api/opencode/models` runs `opencode models` and parses `AgentModel[]` as `Model[]`. The fields don't match — `AgentModel` uses `modelId` not `id`, and `provider` not `provider.name` — so the list is empty or garbage.

2. **Frontend uses wrong hook**: IssueDetail's CoderModelSelector and Settings' DefaultCoderModelSelector both call `useMohistModels()` (returns mohist AI SDK models) instead of `useOpencodeModels()` (returns opencode-compatible models). The `useOpencodeModels` hook exists but is not used anywhere.

3. **ACP ignores `model` parameter**: Both `createAcpConnection` and `runAcpSession` in `acp-session.ts` receive `model` in their options interface but never pass it to opencode. opencode supports `session.setConfigOption({ scope: 'agent', option: 'model', value: ... })` but mohist never calls it. This was partially addressed in Issue #80 and #74 (UI + data storage) but the actual ACP forwarding never landed.

The model priority chain is: `issue.model > stageModels[stage] > opencode.model > default`. Frontend already stores issue.model and stageModels in SQLite, and config.jsonc holds `opencode.model`. The backend is supposed to resolve this chain and pass the final model to opencode via ACP, but it doesn't.

## Goals / Non-Goals

**Goals:**
- Fix `/api/opencode/models` to return correct opencode-compatible `AgentModel[]` objects
- Wire `useOpencodeModels` to both dropdowns (IssueDetail CoderModel + Settings DefaultCoderModel)
- Forward `model` through ACP `setSessionConfigOption` in both `createAcpConnection` and `runAcpSession`
- Ensure `coder_session_started` SSE event carries the `model` field
- Non-blocking: `setSessionConfigOption` failure logs a warning but doesn't abort the session

**Non-Goals:**
- Changing the model priority resolution logic (deferred to a separate issue)
- Modifying `pipeline-model` or workflow stage progression
- Changing the opencode binary or ACP protocol (already supports model param)

## Decisions

### D1: Use `AgentModel` type directly from ACP SDK for discovery response

opencode returns `AgentModel[]` from `models` subcommand. We should parse this as `AgentModel` (with `modelId` and `provider` fields) not `Model` (which has `id`, `name`, `provider.name`).

**Decision**: Create a `OpencodeModelInfo` interface that maps `AgentModel.modelId` → `id` for backward compatibility with the frontend's expected `id` field. The API response shape stays `{ models: OpencodeModelInfo[] }` so the frontend hook and dropdowns don't need changes.

**Alternatives considered:**
- Change frontend to use `AgentModel` fields directly — too invasive, many components expect `id`/`name`
- Keep parsing as `Model[]` and map fields client-side — shifts complexity to frontend

### D2: Forward model via `setSessionConfigOption` after `newSession` succeeds

The ACP `Client` class exposes `setSessionConfigOption(option: SessionConfigOption)` method. The correct call is:
```
connection.setSessionConfigOption({ scope: 'agent', option: 'model', value: model })
```

**Decision**: Call this after `connection.newSession()` returns and before any `session/prompt`. This must be non-blocking — if it fails, log a warning and continue with opencode's default model.

**Alternatives considered:**
- Call `setSessionConfigOption` before `newSession` — opencode requires an active session first
- Throw on failure — would abort sessions which is too aggressive; opencode falling back to its default is acceptable

### D3: No changes to the model priority resolution chain

The priority chain (`issue.model > stageModels[stage] > opencode.model > default`) is already stored in SQLite and config.jsonc. The workflow-controller already passes `model` to `runAcpSession` when it has one. The missing piece is just the ACP forwarding, not the resolution logic. This is deferred to a separate medium-priority issue.

## Risks / Trade-offs

- [Risk] `setSessionConfigOption` is async and must not block `session/prompt` — ensure it's called and awaited before `prompt` but handle rejection gracefully → Mitigation: try/catch with warn log

- [Risk] opencode may not support all model ID formats that mohist AI SDK supports → opencode validates via its own model resolution; mohist just passes the string through. If opencode doesn't recognize it, it falls back to default.

- [Risk] Frontend dropdown may flash empty list while waiting for `/api/opencode/models` → DiscoveryService caches result; still could show loading state

- [Risk] Changing dropdown data source from `useMohistModels` to `useOpencodeModels` may cause visual differences (model names/IDs differ between SDK and opencode) → Accept; the whole point is to show opencode-compatible models

## Migration Plan

1. **Phase 1 — Discovery fix** (`discovery.ts`): Fix `AgentModel[]` parsing, add `OpencodeModelInfo` mapping. Deploy and verify `/api/opencode/models` returns correct list via curl.

2. **Phase 2 — Frontend wiring**: Switch IssueDetail `CoderModelSelector` and Settings `DefaultCoderModelSelector` to `useOpencodeModels`. Verify dropdown shows opencode models in both places.

3. **Phase 3 — ACP forwarding** (`acp-session.ts`): Add `setSessionConfigOption` call after `newSession` in both `createAcpConnection` and `runAcpSession`. Add `model` to `coder_session_started` event.

4. **Phase 4 — Verification**: Run E2E walkthrough, verify model selection actually changes opencode's behavior.

No rollback needed — all changes are additive and backward-compatible.

## Open Questions

1. **What is the correct model priority chain?** Issue #74 implemented `stageModels[stage] > model > issue.model` in UI but backend never wired it. Issue #97 noted the priority bug (issue.model gets overwritten by config default). Need to confirm: should `issue.model` truly win over `opencode.model` config default, or is it the other way around?

2. **Should `setSessionConfigOption` failure log as `error` or `warn`?** Currently spec says warn and continue. Confirm this is acceptable — opencode using its own default is fine, but we want visibility into misconfiguration.

3. **Should we also expose `setSessionConfigOption` for `scope: 'app'` options (e.g., thinking budget, temperature)?** Currently only `scope: 'agent'` + `option: 'model'` is needed. Expanding later is easy.