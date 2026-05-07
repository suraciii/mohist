## Why

User-configured **Coder Model** and **Stage Model Overrides** are persisted in config and exposed in the WebUI, but they never reach the ACP pipeline. Every plan/build/check session silently falls back to opencode's default model because upstream callers (`agent-runner-service`, `server/index.ts`, `conflict-resolution.ts`, `build-stage-runner.ts`) create `acpOptions` with `model` undefined. This is a broken contract: users believe they control the model, but the pipeline ignores their choice entirely.

## What Changes

- Inject `config.opencode.model` into `acpOptions` in all pipeline entry points:
  - `agent-runner-service.ts` (`executeStartPipelineTask` and `executeResumePipelineTask`)
  - `server/index.ts` (`fixBuildErrors` callback in merge queue)
  - `conflict-resolution.ts` (`resolveConflictsViaAgent`)
- Pass `acpOptions.model` into `RalphExecutorContext` in `build-stage-runner.ts` so each build task uses the resolved model.
- Introduce a `resolveStageModel(stage, config)` function implementing the priority chain: `stageModels[stage] > opencode.model > undefined` (falls back to opencode default).
- Integrate stage model resolution into `WorkflowEngine.buildContext` so each stage runner receives the correct `acpOptions.model` without duplicating resolution logic.
- Record `model_selected` events to `workflow_log` when an ACP session successfully sets its model via `setSessionConfigOption`.
- Ensure `coder_session.model` is populated from the upstream `acpOptions.model` value.

## Capabilities

### New Capabilities

- `stage-model-resolution` — Unified model resolution logic that selects the effective LLM model for a given pipeline stage based on `stageModels` overrides, global `opencode.model`, and default fallback.

### Modified Capabilities

- `workflow-log` — Add requirement to emit and persist `model_selected` events when an ACP session configures its model, providing runtime visibility into which model is actually driving each stage.
- `coder-session-tracking` — Add requirement that `coder_session.model` SHALL be populated from the resolved stage model (not left empty), so the WebUI and session history accurately reflect the model in use.

## Impact

| Component | Change |
|-----------|--------|
| `agent-runner-service.ts` | Read `llmConfig.opencode.model` and pass via `acpOptions.model`; read `stageModels` and inject stage-resolved model |
| `workflow-engine.ts` | `buildContext` calls `resolveStageModel(issue.stage, config)` and injects result into `acpOptions.model` |
| `build-stage-runner.ts` | Pass `acpOptions.model` into `RalphExecutorContext.model` |
| `plan-stage-runner.ts` | Receives resolved model from `acpOptions` (no direct change, benefits from upstream fix) |
| `check-stage-runner.ts` | Receives resolved model from `acpOptions` (no direct change, benefits from upstream fix) |
| `server/index.ts` | Read config and pass model into `fixBuildErrors` ACP session options |
| `conflict-resolution.ts` | Read config and pass model into `resolveConflictsViaAgent` ACP session options |
| `agent-runtime/agent-session.ts` | Log `model_selected` workflow_log event after successful `setSessionConfigOption` |
| `workflow/checks/build-test-check.ts` | Already reads `ctx.acpOptions?.model` — becomes effective once upstream injects it |
| `workflow/checks/code-compiles-check.ts` | Already reads `ctx.acpOptions?.model` — becomes effective once upstream injects it |
| Database | `coder_session.model` field populated; `workflow_log` gains `model_selected` entries |
| APIs | No breaking changes; behavior correction only |
| WebUI | No direct changes; model selection UI already exists and will now actually affect execution |
