## Why

Skills currently require manual trigger via REST API (`POST /api/skills/:name/run`), which means routine tasks (daily code audit, weekly dependency check, periodic data cleanup) must be initiated by a human. Adding time-based scheduling to Skills lets users declare `schedule: { every: "24h" }` in SKILL.md and have the server automatically execute them — turning Skills from on-demand tools into always-on automations.

## What Changes

- **New**: Schedule types in SKILL.md `metadata.mohist.schedule` — `every` (interval, optional anchor), `cron` (expression), `at` (one-time)
- **New**: `agent_skill_schedules` table — persist schedule config, `next_run_at`, `last_run_at`, enabled state
- **New**: SchedulerService — loads schedules on server start, computes `next_run_at`, uses `setTimeout` to trigger, recalculates on fire; handles restart recovery with one-time catch-up
- **New**: REST API — `GET /api/agent/schedules`, `PATCH /api/agent/schedules/:skillId` (enable/disable), `POST /api/agent/schedules/refresh`
- **New**: EventBus events — `schedule_triggered`, `schedule_completed`, `schedule_failed`
- **Modified**: Server startup — initialize SchedulerService after SkillService, load persisted schedules, set timers
- **Modified**: DB schema — migration to v16 adding `agent_skill_schedules` table

## Capabilities

### New Capabilities

- `skill-scheduler` — parse schedule config from SKILL.md, persist to SQLite, compute next-run times, fire timers on schedule, recover on restart, expose REST API for control

### Modified Capabilities

- `server-daemon` — start SchedulerService on boot, recover missed executions
- `http-api` — three new endpoints for schedule management
- `event-bus` — new event types for schedule lifecycle

## Impact

- **DB migration**: New `agent_skill_schedules` table (schema v16) in `packages/cli/src/db/migrations.ts`
- **New service**: `packages/cli/src/services/scheduler-service.ts` — core scheduling logic
- **New repo**: `packages/cli/src/db/schedule-repo.ts` — schedule persistence
- **API routes**: New schedule routes in `packages/cli/src/api/`
- **Server startup**: `packages/cli/src/server/` — initialize scheduler, wire into SkillService
- **Dependencies**: Need a cron expression parser (e.g. `cron-parser` or `cronstrue`), or use reference implementation from `opensrc/openclaw/src/cron/parse.ts`
- **Existing behavior preserved**: Skills without `schedule` are unaffected; scheduling is opt-in per SKILL.md
