## Context

The ACP session layer (`agent-session.ts`) already supports a `model` parameter: after `newSession` succeeds, it calls `setSessionConfigOption('model', ...)` to override the opencode default. However, every upstream caller constructs `acpOptions` without `model`, so `options.model` is always `undefined` and the override is silently skipped.

The configuration is already persisted (`config.opencode.model` and `config.opencode.stageModels` in `config.jsonc`) and loaded into `AgentRunnerService.llmConfig`, but that field is written and never read. `stageModels` has zero consumers in the codebase.

Two auto-fix check modules (`build-test-check.ts`, `code-compiles-check.ts`) already forward `ctx.acpOptions?.model` into their ACP sessions, but because upstream never injects it, they also fall back to the default.

## Goals / Non-Goals

**Goals:**
- Wire `config.opencode.model` into every ACP session created by the pipeline and auxiliary paths (merge-queue fix, conflict resolution).
- Implement `stageModels` resolution so each stage can override the global coder model.
- Populate `coder_session.model` and emit `model_selected` workflow-log entries for runtime visibility.
- Make the fix mechanical and low-risk — no refactors of unrelated subsystems.

**Non-Goals:**
- Long-term architecture changes (ModelResolution domain, AcpSessionFactory) — these are noted for future work but out of scope.
- Settings validation or model-availability checks at save time.
- Changing the explore path (it already uses `config.model` via `resolveModel` and is unaffected).
- Modifying the WebUI model-selection components (they already work; only the backend wiring is broken).

## Decisions

### D1: Resolve stage model in `WorkflowEngine.buildContext`

Stage model resolution is performed in one place: `WorkflowEngine.buildContext(issue, acpOptions)`. It reads `issue.stage`, looks up `config.opencode.stageModels[stage]`, falls back to `config.opencode.model`, and writes the result into `acpOptions.model` before passing the context to stage runners.

**Rationale:**
- `issue.stage` is known at this point in the pipeline.
- Centralizing the logic avoids duplicating the priority chain (`stageModels[stage] > opencode.model > undefined`) across `PlanStageRunner`, `BuildStageRunner`, and `CheckStageRunner`.
- `AgentRunnerService` only needs to hand the config object to `WorkflowEngine`; it does not need to know about stage semantics.

**Alternatives considered:**
- *Each StageRunner resolves its own model* — rejected because it creates three copies of the same priority logic and makes it easy to add a new runner that forgets to resolve.

### D2: Pass `ConfigInfo` into `WorkflowEngineOptions`

`WorkflowEngineOptions` gains an optional `config?: ConfigInfo` field. `AgentRunnerService` passes `this.llmConfig` (which is already `ConfigInfo`) when constructing the engine. `buildContext` uses this config to call `resolveStageModel(stage, config)`.

**Rationale:**
- The engine already receives project-level metadata (`projectId`, `issueRepo`, etc.). Adding config is consistent with that pattern.
- `AgentRunnerService` already holds the config; no extra I/O is required.

**Alternatives considered:**
- *Pass only a `modelResolver` function* — rejected as over-abstracted for a single resolution call.
- *Read config inside `WorkflowEngine` via `load()`* — rejected because it bypasses the config already held by the service and ignores the file-watcher reload path.

### D3: Non-pipeline paths read config directly with `load()`

`server/index.ts` (`fixBuildErrors`) and `conflict-resolution.ts` (`resolveConflictsViaAgent`) are outside the `WorkflowEngine`. They call `load()` to obtain the current `ConfigInfo`, extract `config.opencode.model`, and inject it into their `AgentSessionOptions`.

**Rationale:**
- These paths do not stage through `WorkflowEngine`, so they cannot reuse its resolution logic.
- They do not need stage-specific resolution (conflict resolution and merge-queue fix are not tied to a pipeline stage).
- `load()` is cached; the cost is negligible.

**Alternatives considered:**
- *Plumb config through callbacks/deps objects* — rejected because it would require changing the merge-queue and server callback signatures for a single string field.

### D4: `coder_session.model` filled in `WorkflowSessionObserver.onSessionStart`

`WorkflowSessionObserver.onSessionStart` already receives `ctx.model` from `SessionContext` (populated from `_options.model`). The only missing piece is passing `model: ctx.model` into `coderSessionRepo.insert(...)`.

**Rationale:**
- `SessionContext` already carries `model`; no changes to `agent-session.ts` are needed.
- `WorkflowSessionObserver` is the canonical layer for persisting session metadata to the DB.

**Alternatives considered:**
- *Write `model` directly in `AgentSession.create`* — rejected because the observer pattern is the existing boundary for DB side effects.

### D5: `model_selected` logged from `AgentSession.create`

After `setSessionConfigOption('model', model)` succeeds in `AgentSession.create`, the code already logs `log.info('ACP session model set', ...)`. We extend this to also call `wfObserver.writeSessionLog(issueId, 'model_selected', { model, stage, ... })`.

**Rationale:**
- This is the exact point where the model is applied to the ACP session.
- `wfObserver` is already available in that scope and is the canonical way to write to `workflow_log`.

**Alternatives considered:**
- *Add a new observer hook `onModelSet`* — rejected as unnecessary indirection for a single log line.

## Risks / Trade-offs

- **[Risk] `load()` in non-pipeline paths reads from disk each time** → Mitigation: `load()` uses an in-memory cache (`configCache`). The file is only re-read when the cache is cleared (on config write) or the process restarts.
- **[Risk] `WorkflowEngine` now depends on `ConfigInfo`, increasing its interface surface** → Mitigation: The field is optional; existing tests that construct `WorkflowEngine` without config will continue to work (model simply remains `undefined`, preserving current behavior).
- **[Risk] `stageModels` keys are free-form strings; a typo in the config key silently falls back to the global model** → Mitigation: This is user-provided config. We can add validation in a future change; for now, the fallback behavior is safe and predictable.
- **[Risk] `RalphExecutorContext.model` is already plumbed through to `_acpSessionRunner`, but `RalphExecutor` is also used by non-pipeline code** → Mitigation: `model` is optional in `RalphExecutorContext`; callers that do not set it continue to work as before.

## Migration Plan

No migration or rollback needed. This is a pure bug-fix change:
1. Deploy the code change.
2. Existing pipelines will automatically pick up the configured model on the next run.
3. No database migrations are required (existing `coder_session.model` nulls will be filled on new sessions).
4. To verify: start a pipeline, inspect `workflow_log` for `model_selected` entries, and confirm `coder_session.model` is populated.

## Open Questions

### Q1: `issue.model` 字段完全悬空，如何处理？

`Issue` 类型（`types/index.ts:86`）定义了 `model?: string`，API 支持 PATCH 设置，但**全代码库零处读取**。当前 `resolveStageModel` 的优先级链不包含 `issue.model`。

选项：
- **纳入优先级链**（`issue.model > stageModels[stage] > opencode.model`）→ 需新增 per-issue 模型覆盖 UI
- **从 Issue 类型中移除** → 避免半实现状态误导

**本 issue 暂不处理**，保持 `resolveStageModel(stage, config)` 签名不变。后续专门 issue 决定 `issue.model` 的去留。

### Q2: `config.model` 与 `config.opencode.model` 的关系

- `config.model` → SDK 路径（explore、propose），通过 `resolveModel()` 消费
- `config.opencode.model` → ACP 路径（pipeline），通过 `setSessionConfigOption()` 消费

两者是独立配置。用户在 Settings 中配置的 "Default Model" 走 `config.model`，"Coder Model" 走 `config.opencode.model`。

**本 issue 不合并两者**，仅修复 ACP 路径的断裂。
