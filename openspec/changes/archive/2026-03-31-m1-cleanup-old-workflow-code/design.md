## Context

M1 agent architecture is complete and deployed. The old deterministic workflow engine (`workflow/`, `agent/`) and its task-based infrastructure (`TaskRepo`, `WorkflowService`) are dead code. No M1 code path reaches them. The old `Stage` enum includes `WaitingDesignReview` and `WaitingReview` values that M1 never produces. Dead API endpoints (`/approve`, `/resume`, `/pause`) and CLI commands (`issue approve/pause/resume`) still exist but are unreachable in M1.

Current dependency graph of dead code:

```
workflow/engine.ts          ← nobody imports (SAFE)
workflow/issue-workflow.ts  ← engine.ts (dead) + workflow-service.ts (semi-dead)
workflow/stage-handlers.ts  ← engine.ts (dead)
agent/runner.ts             ← engine.ts (dead) + stage-handlers.ts (dead)
agent/prompts.ts            ← runner.ts (dead)

services/workflow-service.ts ← api/issues.ts (approve/resume endpoints, show endpoint)
                                api/issues.ts also uses TaskRepo via StateManager
db/task-repo.ts             ← state-manager.ts, issue-service.ts, api/issues.ts, api/status.ts
types/index.ts              ← everywhere (Stage enum, Task interface)

server/http-server.ts       ← ServerState.activeTasks/queuedTasks (type init)
cli/commands/server.ts      ← status display: activeWorkers/runningTasks/queuedTasks
cli/commands/issue.ts       ← show: progress/stageInfo display; approve: git merge logic
```

## Goals / Non-Goals

**Goals:**
- Remove all dead source files and dead test files
- Remove dead API endpoints and CLI commands
- Remove `TaskRepo` infrastructure from all active code paths
- Remove `WaitingDesignReview`/`WaitingReview` from `Stage` enum
- Remove `Task` interface from types
- Clean `api/status.ts` response to remove stale fields
- Resolve the 4 pre-existing test failures

**Non-Goals:**
- Redesigning the Stage architecture (separate PBI in backlog section 6)
- Implementing pause/resume for M1 (M2 scope)
- Adding new features or capabilities
- Database schema migration framework (just a raw DROP TABLE)

## Decisions

### D1: Inline cascade delete into IssueRepo

**Choice**: Move `taskRepo.deleteByIssue()` / `taskRepo.deleteByProject()` logic into `IssueRepo` as direct SQL (`DELETE FROM tasks WHERE ...`), then remove `TaskRepo` from `IssueService` constructor. Also update `StateManager.deleteProject()` — it currently calls `this.taskRepo.deleteByProject(id)` directly; change it to call `this.issueRepo.deleteByProject(id)` which will include the cascade internally.

**Alternative**: Keep `TaskRepo` just for cascade deletes. Rejected — keeping a 200-line file for 2 SQL statements is wasteful.

### D2: Remove WorkflowService entirely

**Choice**: Delete `services/workflow-service.ts`. The `getProgress()` and `getStageInfo()` data it provides to the show endpoint (`GET /:number`) is based on the old 6-stage model and returns incorrect progress for M1's 3-stage flow. Remove `progress` and `stageInfo` from the show endpoint response.

**Alternative**: Rewrite `WorkflowService` for M1 (3 stages, no approval). Rejected — the show endpoint works fine without progress/stageInfo data. The agent's stage is already in `issue.stage`. Adding a new service just to reformat that would be over-engineering.

### D3: Remove Task table via schema version 3 migration

**Choice**: Add a `migrateToVersion3()` in `db/migrations.ts` that runs `DROP TABLE IF EXISTS tasks` and `DROP INDEX IF EXISTS idx_tasks_project_status`, then bump `SCHEMA_VERSION` to 3. This uses the existing lightweight migration mechanism (version check + function), not a new framework.

**Alternative**: Raw `DROP TABLE` in `state-manager.ts` constructor. Rejected — that would run on every server start without version tracking, making it harder to reason about schema state.

### D4: Remove dead test files entirely

**Choice**: Delete `tests/engine.test.ts` and `tests/agent-runner.test.ts`. Update `tests/api-routes.test.ts`, `tests/e2e.test.ts`, `tests/services.test.ts`, and `tests/api-integration.test.ts` to remove references to deleted code.

**Alternative**: Keep test files with `describe.skip`. Rejected — dead tests are noise.

### D5: Clean ServerState and server CLI status display

**Choice**: Remove `activeTasks`/`queuedTasks` from `ServerState` interface and from `http-server.ts` constructor init. Remove `activeWorkers`/`runningTasks`/`queuedTasks` display from `cli/commands/server.ts` and clean up `fetchServerStatus()` return type. These are downstream consumers of the deleted TaskRepo/status fields.

**Alternative**: Keep them as zeroed/unused fields. Rejected — they reference concepts (tasks, workers) that don't exist in M1, creating confusion.

## Risks / Trade-offs

**[Existing DB rows with waiting-* stages]** → Issues in `waiting-design-review` or `waiting-review` stages from before M1 will still have those string values in SQLite. Removing the enum values doesn't break queries since Stage is stored as a string. However, CLI `formatStage()` won't color them anymore. Acceptable — these are historical artifacts from the old workflow.

**[Show endpoint loses progress data]** → `GET /:number` will no longer return `progress` or `stageInfo`. Clients that rely on these fields will break. Acceptable — no external clients exist in M1, and the stage is still available in `issue.stage`.

**[No rollback for DROP TABLE]** → Once `tasks` table is dropped, the old workflow engine cannot be recovered from a running DB. Acceptable — the old engine code is archived in git and the engine was never run in production.

**[CLI approve command has git merge logic]** → `mo issue approve` performs `git merge --no-ff` when stage is `waiting-review`, then calls cleanup. Deleting this command means historical issues stuck in `waiting-review` must be merged manually. Acceptable — these are pre-M1 artifacts and the command was never used with M1.

**[Server CLI status display loses task/worker info]** → `mo server status` will no longer show `Workers`, `Running tasks`, `Queued tasks` lines. Acceptable — these were always 0 in M1 (no workers, no task queue).

**[Test suite may have more hidden failures]** → The 4 known test failures reference old workflow code. After cleanup, other tests may surface. Mitigation: run full test suite after each phase.
