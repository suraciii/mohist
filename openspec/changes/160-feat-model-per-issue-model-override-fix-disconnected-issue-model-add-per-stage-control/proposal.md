## Why

Issue-level model selection currently gives users a false sense of control: the Web UI persists `issue.model` and reports an active override, but workflow execution ignores it and continues to use only global model configuration. Users also need issue-specific per-stage routing so expensive or capable models can be reserved for the stages and issues that need them, while model discovery must stop creating empty opencode sessions just to populate selectors.

## What Changes

- Honor `issue.model` during workflow model resolution so an issue-level default model actually affects agent sessions.
- Add per-issue per-stage model overrides with the priority chain `issue.stageModels[stage]` > `issue.model` > `config.opencode.stageModels[stage]` > `config.opencode.model` > opencode default.
- Persist issue stage model overrides in the local issue store and include them in issue create, update, and detail flows.
- Extend the issue API to accept and return `stageModels`, validating it as a `Record<string, string>` of `provider/model` values.
- Update Issue Detail model controls to keep the simple single-model override while adding an expandable per-stage override area consistent with Settings.
- Allow issue creation from the Web UI to preset an issue-level model override.
- Correct Web UI stage model lists to use real pipeline stages: `explore`, `plan`, `build`, `check`, and `integrate`; remove the nonexistent `fix` stage.
- Replace model discovery via ACP `newSession()` with the lightweight `opencode models` command and increase discovery cache TTL to reduce process churn and avoid polluting the opencode session list.
- Ensure build-fix and conflict-resolution agent sessions use the same issue-aware model resolution path as normal workflow stages.

## Capabilities

### New Capabilities



### Modified Capabilities

- `local-issue-store`
- `http-api`
- `workflow-engine`
- `agent-runtime`
- `web-ui`

## Impact

- `packages/cli/src/db/migrations.ts` and `packages/cli/src/db/issue-repo.ts`: add `issues.stage_models`, parse and serialize per-issue stage model overrides, and support create/update reads and writes.
- `packages/cli/src/types/index.ts`: extend `Issue` with optional `stageModels`.
- `packages/cli/src/config/model-resolution.ts`: add issue-aware resolution input and enforce the new fallback order.
- `packages/cli/src/workflow/workflow-engine.ts`, `packages/cli/src/services/conflict-resolution.ts`, and `packages/cli/src/server/index.ts`: pass issue-level model configuration into stage, conflict-resolution, and build-fix agent sessions.
- `packages/cli/src/api/issues.ts`: accept `model` and `stageModels` on issue creation, accept `stageModels` on issue updates, and return `stageModels` on issue detail responses.
- `packages/cli/src/services/opencode-discovery-service.ts`: switch discovery from ACP session creation to `opencode models` stdout parsing and extend cache lifetime.
- `packages/cli/web/src/components/IssueModelSelector.tsx`: expose per-stage issue overrides while preserving the single default model selector.
- `packages/cli/web/src/components/AiSettingsSection.tsx`: correct the stage model override stage list.
- `packages/cli/web/src` issue creation flow and API/types helpers: allow creating an issue with a preset model and carry `stageModels` through client types.
- External behavior: no breaking API removal is expected, but existing persisted `issue.model` values begin taking effect for workflow execution instead of being display-only.
