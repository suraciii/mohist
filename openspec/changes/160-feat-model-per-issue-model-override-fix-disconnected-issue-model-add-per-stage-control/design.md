## Context

Per-issue model override was partially implemented: `issues.model` exists, the Issue Detail page can update it, and the UI reports an active override. The workflow execution path does not read that value because `WorkflowEngine.buildContext()` calls `resolveStageModel(stage, config)` with only global config, so the stored issue override is display-only.

Global per-stage model routing already exists through `config.opencode.stageModels`, but the same mechanism cannot be scoped to one issue. Model discovery also currently starts `opencode acp` and calls `newSession()` only to read `availableModels`, which creates persistent empty opencode sessions.

Constraints:

- Existing global config storage stays in `config.jsonc`.
- Agent sessions still receive the selected model through the existing `AgentSessionOptions.model` / opencode ACP configuration path.
- Issue-level configuration is local issue metadata stored in SQLite.
- UI stage override controls should match the Settings page mental model.

## Goals / Non-Goals

**Goals:**

- Make existing `issue.model` values affect workflow, build-fix, and conflict-resolution agent sessions.
- Add per-issue per-stage model overrides without duplicating model selection rules across callers.
- Preserve simple issue-level default selection while exposing advanced per-stage overrides.
- Correct stage override UI lists to use real executable pipeline stages: `explore`, `plan`, `build`, `check`, `integrate`.
- Stop model discovery from creating opencode sessions.
- Keep unset values as true fallbacks rather than materializing defaults into issue records.

**Non-Goals:**

- Do not change global `opencode.stageModels` storage or semantics.
- Do not add CLI model flags for issue creation or updates.
- Do not change the opencode ACP model-setting mechanism used for real agent sessions.
- Do not clean up already-created empty opencode sessions.
- Do not validate model IDs against the live opencode model list; only validate the persisted shape and `provider/model` format.

## Decisions

### D1: Keep Model Precedence In `resolveStageModel`

`resolveStageModel` will accept an optional issue-level override object:

```ts
type IssueModelOverride = {
  model?: string | null
  stageModels?: Record<string, string> | null
}
```

The resolver will return the first configured value in this order:

1. `issueOverride.stageModels?.[stage]`
2. `issueOverride.model`
3. `config.opencode.stageModels?.[stage]`
4. `config.opencode.model`
5. `undefined`

This keeps precedence rules in one deep module instead of spreading conditional fallback logic across the workflow engine, merge conflict recovery, build-fix callback, and future callers.

**Alternatives considered:**

- Resolve issue overrides directly in `WorkflowEngine.buildContext()`: rejected because conflict-resolution and build-fix paths would need to duplicate the same chain.
- Store the effective model on the issue before each stage starts: rejected because it would persist derived state and make fallback behavior stale when global config changes.
- Make issue per-stage overrides replace the entire global stage model map: rejected because unset issue stages should continue falling back stage-by-stage.

### D2: Store Per-Issue Stage Overrides As Nullable JSON Text

Add `issues.stage_models TEXT DEFAULT NULL`. `IssueRepo` parses it into `Issue.stageModels?: Record<string, string>` and serializes updates with `JSON.stringify`. `NULL` means no per-stage override; an empty object should be normalized to `NULL` when practical so fallback behavior remains unambiguous.

This matches existing JSON-in-TEXT patterns such as `labels` and `approval_state` while avoiding a separate child table for a small sparse map keyed by stage name.

**Alternatives considered:**

- Create an `issue_stage_models` table with one row per stage: rejected as unnecessary relational complexity for a bounded, low-cardinality map.
- Add one column per stage: rejected because stage names are workflow data and the schema would need changes whenever executable stages change.
- Store stage overrides only in config files: rejected because the setting is issue metadata and must travel with issue create/update/detail flows.

### D3: Validate API Shape At The Boundary

`POST /api/issues` and `PATCH /api/issues/:number` will accept `model` and `stageModels`. API handlers should validate:

- `model` is `string` containing `/`, `null`, or omitted.
- `stageModels` is an object, `null`, or omitted.
- Each `stageModels` value is a string containing `/`.
- Stage keys are strings; known executable stage keys are preferred in UI, but the backend does not need to reject unknown keys unless an existing validation helper already does so.

The repository layer should assume validated data and focus on persistence. This keeps user-facing errors near the HTTP boundary and avoids turning the storage layer into an API validator.

**Alternatives considered:**

- Validate model IDs by calling model discovery: rejected because discovery can fail or be slow, and a configured model may be temporarily unavailable while still intentionally persisted.
- Validate stage keys strictly against the current `Stage` enum: rejected for now because custom workflow stages may become possible and unknown keys are harmless unless selected by a caller.
- Put all validation in `IssueService`: rejected because current issue routes already validate similar fields such as `priority` and `model` at the API boundary.

### D4: Treat Workflow, Conflict Resolution, And Build Fix As The Same Model-Resolution Use Case

Normal stage execution will call `resolveStageModel(issue.stage, config, issue)`. Conflict resolution and build-error fix sessions will load or receive the relevant issue and call `resolveStageModel(Stage.Build, config, issue)` because those recovery sessions are build-stage work even when their observer stage label is more specific.

This makes `issue.model` and `issue.stageModels.build` apply consistently to all coder sessions spawned for the issue.

**Alternatives considered:**

- Use each issue's current stage for conflict/build-fix recovery: rejected because a merge conflict or build failure recovery session semantically needs the build model policy, not whichever lifecycle stage currently stores the blocked state.
- Add separate pseudo-stages such as `conflict-resolution` or `build-fix` to stage model config: rejected because the user-facing requirement is per pipeline stage control, and introducing hidden stages would complicate the UI.

### D5: Discover Models With `opencode models`

Replace ACP discovery with `execFile(binPath, ['models'])`. The service will parse stdout into the same `provider/model` strings returned today. It should continue using `resolveOpencodeBinPath()` and continue filtering inherited server auth env if that filtering remains relevant for child opencode commands. Cache TTL increases from 5 minutes to 30 minutes.

This avoids creating persistent opencode sessions for a read-only model list and makes the discovery service a lightweight CLI query rather than an ACP lifecycle participant.

**Alternatives considered:**

- Keep ACP discovery and delete the created session afterward: rejected because it depends on opencode internals and still creates avoidable database writes.
- Cache forever after first discovery: rejected because users may change provider/model configuration during a running server session.
- Read opencode's database or config directly: rejected because it couples Mohist to opencode storage internals.

### D6: Reuse The Settings Per-Stage UI Pattern On Issue Detail

`IssueModelSelector` remains the simple default-model control and gains an advanced expandable section for stage overrides. It receives both `currentModel` and `currentStageModels`, updates `PATCH /api/issues/:number`, and invalidates the issue queries after changes. The stage list is a shared constant or duplicated minimally as `['explore', 'plan', 'build', 'check', 'integrate']`, matching actual executable pipeline stages and Settings.

Create Issue remains simple: allow selecting an optional default model at creation time, but do not expose per-stage overrides in the create dialog.

**Alternatives considered:**

- Put all issue model controls in Settings: rejected because issue-specific routing must be visible where the issue is managed.
- Expose per-stage controls by default: rejected because most users only need one issue default, and always showing five selectors increases cognitive load.
- Add per-stage controls to create issue: rejected for initial scope because creation should stay lightweight and users can refine advanced overrides after the issue exists.

## Risks / Trade-offs

- [Existing `issue.model` values will start affecting execution after upgrade] → This is intended bug-fix behavior; make it visible in release notes or task summary because users may have stale overrides they forgot about.
- [Invalid JSON in `stage_models` could break issue reads] → Parse defensively like `labels` and treat malformed values as no overrides.
- [Unknown stage keys may be persisted] → They are ignored unless passed to `resolveStageModel`; UI only emits known executable stage keys.
- [The `opencode models` output format may differ across opencode versions] → Parse conservatively by accepting lines that look like `provider/model`, log discovery failures, and preserve existing API error behavior.
- [Multiple places can update issue model metadata] → Keep model metadata in the existing `IssueRepo.update` path so API, service, and future callers share one write implementation.
- [Frontend has two model selector implementations] → Reuse existing primitives where practical, but avoid a broad UI refactor in this change.

## Migration Plan

1. Increment SQLite schema version and add a migration that adds `issues.stage_models TEXT DEFAULT NULL` if missing.
2. Update initial table creation or compatibility migrations so new databases include all issue model columns, including `model` and `stage_models`.
3. Extend shared backend and frontend `Issue` types with `stageModels?: Record<string, string>`.
4. Update `IssueRepo` create, row mapping, and update paths to handle `model` and `stageModels` together.
5. Extend issue API create/update validation and pass validated model metadata through `IssueService` to `IssueRepo`.
6. Update `resolveStageModel` and all callers to provide issue overrides where an issue is available.
7. Replace discovery implementation with `opencode models` parsing and adjust cache TTL.
8. Update Web UI types, API client, Issue Detail model controls, Settings stage list, and Create Issue model preset.
9. Add or update tests for model precedence, issue persistence/API validation, and discovery behavior.

Rollback strategy:

- Code rollback is safe because `stage_models` is additive and nullable.
- Older code that uses `SELECT *` with an extra SQLite column should continue to work as long as it does not require the field.
- If `opencode models` is unavailable in a deployed environment, rollback only the discovery service change or add a guarded fallback that does not create ACP sessions unless explicitly enabled.
