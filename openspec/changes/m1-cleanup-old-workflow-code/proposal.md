## Why

M1 agent architecture (agent-runtime + agents + tools) is complete and working. The old deterministic workflow engine (workflow/ + agent/ directories) and its task-based infrastructure (TaskRepo, Task table, WorkflowService) are dead code — no M1 code path reaches them. Leaving them creates confusion about which system is active, inflates the codebase, and causes the 4 pre-existing test failures.

## What Changes

- Delete old workflow engine files: `workflow/engine.ts`, `workflow/issue-workflow.ts`, `workflow/stage-handlers.ts`
- Delete old agent runner files: `agent/runner.ts`, `agent/prompts.ts`
- Delete dead test files: `tests/engine.test.ts`, `tests/agent-runner.test.ts`
- Remove `WorkflowService` and its old `approve`/`STAGE_ORDER`/`getProgress`/`getStageInfo` logic
- Remove dead API endpoints: `POST /:number/approve`, `POST /:number/resume`, `POST /:number/pause` (already 501)
- Remove dead CLI commands: `issue approve`, `issue pause`, `issue resume`
- Remove `TaskRepo` from all active code paths (`StateManager`, `IssueService`, `api/issues.ts`, `api/status.ts`)
- Delete `db/task-repo.ts` and the `tasks` table
- Remove `WaitingDesignReview` and `WaitingReview` from `Stage` enum
- Remove `Task` interface and `ServerState.activeTasks`/`queuedTasks` from `types/index.ts`
- Clean `api/status.ts` response (remove stale task counts and waiting-stage counts)
- Clean `server/http-server.ts` (remove `activeTasks`/`queuedTasks` from `ServerState` init)
- Clean `cli/commands/server.ts` (remove `activeWorkers`/`runningTasks`/`queuedTasks` display)
- Clean `cli/commands/issue.ts` show command (remove `progress`/`stageInfo` display)
- Update remaining tests to remove references to deleted code

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `http-api`: Remove approve/resume/pause endpoints, remove task-related fields from status response, remove WorkflowService dependency from issue routes, remove ServerState task fields
- `workflow-engine`: Update spec to reflect M1 3-stage model (remove waiting-* stages from Stage enum, remove Task infrastructure)
- `cli-interface`: Remove approve/pause/resume subcommands from issue CLI, remove progress/stageInfo from show display, remove task/worker info from server status display

## Impact

- **Code deletion**: ~7 source files, ~2 test files, ~12 files modified
- **API breaking change**: `POST /:number/approve`, `POST /:number/resume`, `POST /:number/pause` endpoints removed (M1 already returns 501 for pause)
- **API breaking change**: `GET /:number` no longer returns `progress` or `stageInfo`; `GET /status` no longer returns `runningTasks`, `queuedTasks`, `activeWorkers`
- **CLI breaking change**: `issue approve`, `issue pause`, `issue resume` commands removed
- **CLI breaking change**: `mo server status` no longer shows workers/tasks/queued lines
- **Database migration**: DROP TABLE `tasks` (via schema version 3)
- **Types**: `Stage` enum loses 2 values, `Task` interface removed, `ServerState` loses `activeTasks`/`queuedTasks`
- **Tests**: 4 pre-existing test failures resolved; remaining tests updated to remove old references
