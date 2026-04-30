## Context

mohist server runs as a long-lived Node.js process. Skills (defined in `.mohist/skills/*/SKILL.md`) are currently triggered manually via `POST /api/skills/:name/run`. Issue #99 introduces the Skill execution infrastructure (SkillService, SkillRepo, SkillRunRepo, skill REST API). This change adds time-based scheduling on top of that infrastructure.

The server is a single-process app using `better-sqlite3` for persistence, `Hono` for HTTP routing, and a custom `EventBus` for pub/sub. There is no external message queue or job runner. The `server/index.ts` main function wires all services together on startup.

Reference implementation: `opensrc/openclaw/src/cron/` — a production cron system with timer management, restart catch-up, and error backoff. We adopt its core patterns (single timer per service, clamped delays, catch-up on restart) but simplify heavily since mohist has 1–10 skills, not thousands of cron jobs.

### Current State

- **No skill code exists yet** — #99 will introduce SkillService, SkillRepo, SkillRunRepo, and skill API routes
- **Server startup** (`server/index.ts`): creates all services, wires routes, starts HTTP listener
- **EventBus** (`services/event-bus.ts`): synchronous in-process pub/sub with typed `EventMap`
- **SSE events** (`api/events.ts`): subscribes to ALL_EVENT_TYPES array, forwards to SSE clients
- **DB migrations** (`db/migrations.ts`): at schema v15, incremental versioned migrations
- **StateManager** (`server/state-manager.ts`): repo factory, creates repos in constructor

## Goals / Non-Goals

**Goals:**
- Parse `schedule` config from SKILL.md and persist to `agent_skill_schedules` table
- Compute `next_run_at` for three schedule types: `every`, `cron`, `at`
- Fire skill execution at the scheduled time using `setTimeout`
- Recover schedules on server restart with one catch-up execution for missed runs
- Expose REST API to list, enable/disable, and refresh schedules
- Emit EventBus events for schedule lifecycle (triggered/completed/failed)

**Non-Goals:**
- Distributed scheduling across multiple server instances (single-process only)
- File watcher for auto-reloading SKILL.md changes (use manual `/api/agent/schedules/refresh` instead)
- Error backoff / retry logic for failed executions (just log and schedule next)
- UI for schedule management (API only in this change)
- Persistent job queue (in-memory `setTimeout` + SQLite recovery is sufficient)

## Decisions

### D1: Single global timer (openclaw pattern) vs per-schedule timers

**Choice: Per-schedule `setTimeout`.**

Each enabled schedule gets its own `setTimeout` set to the delay until its `next_run_at`. On fire, the timer is re-armed with the new delay.

**Rationale:** mohist will have very few scheduled skills (1–10). Per-schedule timers are simpler to reason about and don't require the "find earliest due job" scan that openclaw's single-timer approach uses (which is optimized for thousands of jobs). Per-schedule timers also make enable/disable O(1) — just clear the timer.

**Alternatives considered:**
- Single global timer that fires every N seconds and scans for due jobs (openclaw approach) — better at scale, unnecessary complexity here
- `setInterval` instead of `setTimeout` — drifts over time, harder to adjust on config change

### D2: Cron expression parsing library

**Choice: Use `cron-parser` npm package.**

**Rationale:** The `cron-parser` package is lightweight (~10KB), well-maintained, and handles standard 5-field cron expressions correctly. Writing our own parser would duplicate work that's easy to get wrong (timezone handling, day-of-week semantics, range expressions like `1-5`).

**Alternatives considered:**
- Vendor openclaw's `parse.ts` — only handles absolute timestamps, not cron expressions
- Write from scratch — high risk for edge cases (month boundaries, DST, etc.)
- `node-cron` — more than we need (includes its own scheduler)

### D3: Interval parsing

**Choice: Implement a simple `parseDuration()` utility.**

Parse duration strings like `"30m"`, `"24h"`, `"1d"` into milliseconds using regex. Support units: `s`, `m`, `h`, `d`.

**Rationale:** Only 4 units needed. No need for a library. The `anchor` feature for `every` schedules uses a simple "next occurrence of HH:MM" calculation.

**Alternatives considered:**
- `ms` npm package — adds a dependency for trivial functionality
- ISO 8601 duration (`PT30M`) — less readable in SKILL.md frontmatter

### D4: Timer delay clamping

**Choice: Clamp timer delays to max 60 seconds (like openclaw's `MAX_TIMER_DELAY_MS`).**

When `next_run_at` is >60s away, set a 60s timer that re-checks. This prevents timer drift from process suspension (sleep, SIGSTOP) and keeps the scheduler responsive to enable/disable/refresh operations.

**Rationale:** openclaw uses the same pattern. Node.js `setTimeout` with very long delays (>24 days) can be unreliable, and process suspension is a real concern for long-running servers.

**Alternatives considered:**
- No clamping, trust `setTimeout` — can miss fires after process wake-up
- Use `setInterval(60s)` as a global tick — same effect but less precise

### D5: Catch-up strategy on restart

**Choice: Fire one catch-up execution per overdue schedule, then schedule normally.**

On startup, for each enabled schedule where `next_run_at < now`, execute immediately and compute the next run from now (not from the missed time).

**Rationale:** Matches the spec requirement ("错过的执行补跑一次"). openclaw's approach is more sophisticated (stagger, max missed per restart) but unnecessary for our low volume.

**Alternatives considered:**
- Skip missed executions entirely — user expectation is that scheduled tasks run
- Run all missed executions since last run — could cause a burst of executions after a long downtime

### D6: Integration with SkillService (#99)

**Choice: SchedulerService calls `SkillService.runSkill(skillName)` directly.**

The scheduler reuses the same execution path as manual triggers. The skill run record (in `skill_runs` from #99) gets an additional `trigger_source: "schedule"` field to distinguish from manual runs.

**Rationale:** No need for a separate execution path. The same Issue creation, event emission, and error handling apply.

**Alternatives considered:**
- Emit an event that SkillService listens to — adds indirection for no benefit
- Queue-based approach — overkill for single-process

### D7: DB schema — `agent_skill_schedules` references `agent_skills`

**Choice: `skill_id TEXT NOT NULL REFERENCES agent_skills(id)`.**

The schedule table has a foreign key to the skills table (from #99). When a skill is deleted, its schedule is cascade-deleted.

**Rationale:** Maintains referential integrity. If a skill is removed from disk and its row deleted from `agent_skills`, the schedule should also disappear.

## Risks / Trade-offs

**[Timer drift on long-running server]** → 60-second clamping (D4) ensures we re-check frequently enough to catch drift. Acceptable for minute-level precision.

**[No file watcher for SKILL.md changes]** → Users must call `POST /api/agent/schedules/refresh` after editing schedule config. Acceptable for v1; can add file watcher later.

**[Single process limitation]** → If the server crashes, in-memory timers are lost. Mitigated by SQLite persistence and restart catch-up (D5).

**[Catch-up burst after long downtime]** → At most one catch-up per schedule. If server was down for 3 days with a daily schedule, only one run fires on restart, not three. This is by design — better to under-run than overload.

**[External dependency: `cron-parser`]** → Minimal risk; well-established package. If it becomes unmaintained, the interface is small enough to swap.

## Migration Plan

1. **Add `cron-parser` dependency** to `packages/cli/package.json`
2. **DB migration to v16** — add `agent_skill_schedules` table in `migrations.ts`
3. **New files:**
   - `src/db/schedule-repo.ts` — CRUD for `agent_skill_schedules`
   - `src/services/scheduler-service.ts` — core scheduling logic
   - `src/services/schedule-parser.ts` — `parseDuration()`, `computeNextRun()` helpers
   - `src/api/schedules.ts` — REST API routes
4. **Modify existing files:**
   - `server/index.ts` — instantiate SchedulerService, wire into startup
   - `services/event-bus.ts` — add schedule event types to EventMap
   - `api/events.ts` — add schedule events to ALL_EVENT_TYPES
   - `server/state-manager.ts` — add ScheduleRepo getter
   - `db/index.ts` — export ScheduleRepo
5. **Tests:** unit tests for `schedule-parser.ts`, integration tests for SchedulerService lifecycle

### Rollback

- Migration v16 adds a new table only — safe to leave in place if feature is rolled back
- SchedulerService can be disabled by simply not initializing it in `server/index.ts`
- No breaking changes to existing tables or APIs

## Open Questions

- **Should `anchor` timezone be configurable?** Currently defaults to system local time. Could add `timezone` field to schedule config (e.g. `schedule: { every: "24h", anchor: "09:00", timezone: "Asia/Shanghai" }`). Out of scope for v1.
