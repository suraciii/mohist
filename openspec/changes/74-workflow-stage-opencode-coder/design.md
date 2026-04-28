## Context

Currently `runAcpSession` and `createAcpConnection` in `packages/cli/src/agent-runtime/acp-session.ts` spawn `opencode acp` and immediately call `initialize` → `newSession` → `prompt` with no model override. The opencode binary's built-in default model is used for every session. Users cannot configure per-stage models, discover available models, or get visibility into which model was selected.

The change adds a discovery service, config schema extensions, and model-override logic in the ACP session lifecycle.

## Goals / Non-Goals

**Goals:**
- Discover available opencode models via a short-lived ACP probe, cached 5 minutes
- Expose discovery via `GET /api/opencode/models`
- Allow `opencode.model` (global default) and `opencode.stageModels` (per-stage overrides) in `~/.mohist/config.jsonc`
- Apply model override after `newSession` and before `prompt` via `setSessionConfigOption`
- Validate configured models against discovered list before spawning
- Fall back gracefully on runtime model failure and log `model_selected` / `model_fallback` events
- Keep `runAcpSession` and `createAcpConnection` as the primary entry points

**Non-Goals:**
- Changing how opencode itself is configured (auth, credentials, bin path) — existing mechanisms remain
- Multi-project model isolation (project-level config) — P1
- Cost aggregation — P2
- Interactive CLI model configuration — P1

## Decisions

### D1: Probe via `initialize` → `newSession({ cwd })` for model discovery

`newSession` returns `availableModels` in its response payload. A short-lived ACP process (just initialize + newSession, then kill) gives the full list without needing to parse opencode's config files or environment.

**Alternatives considered:**
- Parse `~/.opencode/auth.json` or `~/.opencode/config.jsonc` directly — fragile, format not stable
- `opencode models list` CLI — not a known command

### D2: In-process memory cache for discovery (no Redis, no DB)

Model availability doesn't change frequently. A 5-minute `Map`-based TTL cache in the discovery service singleton is sufficient and avoids adding persistence dependencies.

### D3: Model override via `setSessionConfigOption` after `newSession`

The issue describes this as PoC-verified. After `newSession`, before the first `prompt`, we call:
```
connection.setSessionConfigOption({ configId: "model", value: "provider/model" })
```
This avoids needing to pass model through command-line flags.

**Alternatives considered:**
- Pass model via `cwd` or env var — opencode doesn't support this for ACP sessions
- Edit opencode's local config before each spawn — too invasive

### D4: Stage field added to `AcpSessionOptions`

`AcpSessionOptions.stage?: string` is added so the caller (`AgentRunnerService`) can pass the current workflow stage when spawning. The discovery service is consulted to resolve the effective model.

**Alternatives considered:**
- Look up the stage from `issueId` inside `runAcpSession` — introduces DB coupling
- Resolve model in `AgentRunnerService` before calling `runAcpSession` — cleaner; decided to resolve outside to keep `runAcpSession` unchanged for callers that don't care about models

### D5: Two new `workflow_log` event types

`model_selected` and `model_fallback` are recorded with the effective model, source (stageModels/opencode.model/default), and fallback reason. No new DB table — extends `workflow_log.event_type` enum.

## Risks / Trade-offs

- [Risk] `setSessionConfigOption` is a newer ACP method — if the opencode version doesn't support it, the call will fail or no-op. **Mitigation**: catch errors on the `setSessionConfigOption` call; if it fails and a non-default model was configured, fall back to default and log `model_fallback`.
- [Risk] Discovery probe competes with real sessions for opencode process slots. **Mitigation**: discovery probe is a separate short-lived process; TTL of 5 min limits frequency.
- [Risk] Config file (`~/.mohist/config.jsonc`) is loaded once at startup. Model config changes require server restart. **Mitigation**: acceptable for initial implementation; cache refresh command is P1.
- [Risk] `stage` values in `stageModels` use mohist stage names (`plan`, `build`, `check`) but the issue example uses `design`. **Decision**: use mohist stage enum values (`plan | build | check`; `draft`/`done` don't spawn coder sessions). Warn on unknown keys.

## Migration Plan

1. Add `opencode.model` and `opencode.stageModels` to `ConfigInfoSchema` in `config-schema.ts`
2. Create `opencode-discovery-service.ts` with cached probe
3. Add `GET /api/opencode/models` route
4. Add `stage?: string` to `AcpSessionOptions`
5. In `runAcpSession` and `createAcpConnection`: after `newSession`, resolve model, validate, call `setSessionConfigOption`, log `model_selected`
6. Wrap `setSessionConfigOption` in try/catch; on failure with a non-default model, log `model_fallback` and continue without override
7. Deploy: no migration needed; backward-compatible when config keys absent

## Open Questions

- Should the discovery service be initialized eagerly at server startup or lazily on first `GET /api/opencode/models` request? Lazy is simpler, but a failed probe leaves the API returning 503 until a successful call. Eager ensures the cache is warm but slows startup. **Decision**: lazy with background warm-up on server start.
