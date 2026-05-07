# Review Report

## Result: PASS

## Dimensions

### Correctness — PASS

`resolveStageModel` priority chain is correct (`packages/cli/src/config/model-resolution.ts:13-22`). `stageModels[stage] > opencode.model > undefined` implemented correctly with `stage in stageModels` existence check, so explicit `undefined` values in stageModels are handled properly. Case-sensitive matching is tested and documented.

Pipeline model injection is wired end-to-end:
- `agent-runner-service.ts:1012` passes `this.llmConfig` (ConfigInfo) into `WorkflowEngine` constructor
- `workflow-engine.ts:69` resolves stage model via `resolveStageModel(issue.stage, this.config)` and injects into `acpOptions.model`
- `build-stage-runner.ts:156` forwards `acpOptions.model` into `RalphExecutorContext.model`
- `ralph-executor.ts:712` passes `context.model` into `_acpSessionRunner`

Non-pipeline paths inject model correctly:
- `server/index.ts:162-175` `fixBuildErrors` callback calls `loadConfig()` and passes `config.opencode?.model`
- `conflict-resolution.ts:34-47` `resolveConflictsViaAgent` calls `loadConfig()` and passes `config.opencode?.model`

Runtime visibility implemented:
- `session-observer.ts:95` `WorkflowSessionObserver.onSessionStart` passes `ctx.model` into `coderSessionRepo.insert`
- `agent-session.ts:338-340` writes `model_selected` to `workflow_log` after successful `setSessionConfigOption`

Backward compatibility preserved:
- `WorkflowEngineOptions.config` is optional; existing code without config continues to work
- When `config` is absent, pre-existing `acpOptions.model` is preserved (`workflow-engine.test.ts:151-161`)
- When `model` is undefined, `AgentSession.create` skips `setSessionConfigOption` (preserves existing behavior)

### Complexity — PASS

Functions are small and focused. `resolveStageModel`: 6 lines. `WorkflowEngine.buildContext` model injection: 1 line (`resolveStageModel` call). No cyclomatic complexity issues introduced.

### Test Coverage — PASS

Comprehensive tests added:
- `tests/config/model-resolution.test.ts` (10 tests): covers stage override, global fallback, undefined config, case sensitivity, explicit undefined values
- `tests/workflow/workflow-engine.test.ts` (5 tests): covers model injection per stage, fallback behavior, config absence, pre-existing model preservation

All tests pass:
- Build: `npm run build` passes
- Tests: 1269 passed, 6 skipped

### Security — PASS

No injection risks. `model` is treated as an opaque string identifier; no shell command construction or SQL interpolation. `loadConfig()` is the existing cached config loader; no new I/O paths introduced.

### Spec Compliance — PASS

| Acceptance Criterion | Status | Evidence |
|---|---|---|
| 设置 Coder Model 后，plan/build/check 阶段实际使用该模型 | **PASS** | `agent-runner-service.ts:1012` → `workflow-engine.ts:69` → `build-stage-runner.ts:156` → `ralph-executor.ts:712` |
| 设置 Stage Model Overrides 后，对应 stage 使用覆盖模型，其他 stage 使用全局 Coder Model | **PASS** | `model-resolution.ts:13-22` implements `stageModels[stage] > opencode.model`; `workflow-engine.test.ts:94-125` verifies per-stage injection |
| 未设置任何模型时，行为与现在一致（使用 opencode 默认） | **PASS** | `agent-session.ts:332` skips `setSessionConfigOption` when `model` is falsy; `workflow-engine.ts:69` uses conditional spread so undefined doesn't overwrite |
| `coder_session` 表中的 `model` 字段被正确填充 | **PASS** | `session-observer.ts:95` passes `ctx.model` to insert |
| `workflow_log` 中可查询到 `model_selected` 事件（或等效记录） | **PASS** | `agent-session.ts:338-340` writes `model_selected` event |
| Web UI 中 pipeline 运行时可显示当前 session 使用的模型 | **PASS** | Backend now populates `coder_session.model` and emits `model_selected`; UI already displays `coder_session` data. No frontend changes needed in this fix. |
| auto-fix（build-test-check / code-compiles-check）也使用对应模型 | **PASS** | These paths already read `ctx.acpOptions?.model` and pass to `runAcpSession`; upstream fix in `workflow-engine.ts:69` makes them effective |
| merge queue fixBuildErrors 和 conflict resolution 也使用对应模型 | **PASS** | `server/index.ts:174` and `conflict-resolution.ts:46` both inject `config.opencode?.model` |

## Fix Suggestions

No error-level issues found. Minor observations:

1. **Case sensitivity of stage keys**: `resolveStageModel` is case-sensitive, which is documented and tested. Stage enum values are lowercase (`'plan'`, `'build'`, `'check'`), so this is consistent. Users configuring `stageModels` in JSON must use lowercase keys.

2. **`issue.model` remains unused**: Acknowledged in design.md (Q1) as out of scope. The field is defined in `Issue` type but zero consumers. A future issue should decide whether to remove it or incorporate it into the priority chain.

3. **No model availability validation**: If a user configures a non-existent model, the pipeline will fail at `setSessionConfigOption` time with a warning log but no user-facing error. This is acceptable for a bug-fix scope but noted in the proposal's long-term recommendations.

<promise>PASS</promise>
