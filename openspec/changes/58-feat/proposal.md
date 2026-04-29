## Why

Agent and workflow timeouts are hardcoded across three locations (`ralph-executor.ts`, `workflow-loader.ts`, `acp-session.ts`), making it impossible for users to adjust behavior for large codebases, slow networks, or constrained CI environments. Exposing these as user-configurable values is a prerequisite for reliable operation across diverse environments.

## What Changes

- Add `agent.taskTimeout` (default 600s), `agent.stageTimeout` (default 3600s), and `agent.maxGracePeriods` (default 2) to the `~/.mohist/config.jsonc` schema
- Replace hardcoded timeout constants in `ralph-executor.ts` (`DEFAULT_TASK_TIMEOUT_MS`, `MIN_TASK_TIMEOUT_MS`) with values resolved from config
- Replace hardcoded stage timeouts in `workflow-loader.ts` (`DEFAULT_WORKFLOW.stages[].timeout`) with config-driven defaults
- Add validation for timeout values (positive, reasonable upper bounds) in `config-service.ts`
- Config changes take effect on next issue start — no server restart required

## Capabilities

### New Capabilities

- **agent-timeout-config**: User-configurable timeout settings for agent task execution and workflow stages, with validation and sensible defaults.

### Modified Capabilities

- **ralph-task-execution**: Per-task timeout (`perTaskTimeout`) and max retry logic will be driven by config values instead of hardcoded constants.
- **workflow-definition**: Default stage timeouts will fallback to config values when not specified in a project's `workflow.yaml`.

## Impact

- **Files**: `config-schema.ts`, `config-loader.ts`, `config-service.ts`, `ralph-executor.ts`, `workflow-loader.ts`, `workflow-controller.ts`
- **API**: `GET /api/config` and `PUT /api/config` will expose new keys
- **Storage**: Timeout values stored in `~/.mohist/config.jsonc` (no DB migration needed)
- **Backward compatible**: All new config keys have defaults matching current hardcoded values
