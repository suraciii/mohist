## ADDED Requirements

### Requirement: Schedule config parsed from SKILL.md

SchedulerService SHALL parse `metadata.mohist.schedule` from SKILL.md frontmatter. The schedule field SHALL support three types: `every` (interval with optional anchor), `cron` (cron expression), and `at` (one-time ISO timestamp).

#### Scenario: Parse every interval

- **WHEN** a SKILL.md contains `metadata.mohist.schedule: { every: "30m" }`
- **THEN** SchedulerService SHALL create a schedule with `schedule_type = "every"` and `schedule_value = "30m"`
- **AND** `next_run_at` SHALL be computed as now + 30 minutes

#### Scenario: Parse every interval with anchor

- **WHEN** a SKILL.md contains `metadata.mohist.schedule: { every: "24h", anchor: "09:00" }`
- **THEN** SchedulerService SHALL create a schedule with `schedule_type = "every"`, `schedule_value = "24h"`, and `anchor = "09:00"`
- **AND** `next_run_at` SHALL be the next occurrence of 09:00 in the project's local timezone

#### Scenario: Parse cron expression

- **WHEN** a SKILL.md contains `metadata.mohist.schedule: { cron: "0 9 * * 1-5" }`
- **THEN** SchedulerService SHALL create a schedule with `schedule_type = "cron"` and `schedule_value = "0 9 * * 1-5"`
- **AND** `next_run_at` SHALL be the next matching time according to standard cron semantics

#### Scenario: Parse one-time schedule

- **WHEN** a SKILL.md contains `metadata.mohist.schedule: { at: "2026-05-01T10:00:00Z" }`
- **THEN** SchedulerService SHALL create a schedule with `schedule_type = "at"` and `schedule_value = "2026-05-01T10:00:00Z"`
- **AND** `next_run_at` SHALL be set to that timestamp

#### Scenario: Invalid schedule config

- **WHEN** a SKILL.md contains an invalid schedule (e.g. `{ every: "abc" }` or `{ cron: "invalid" }`)
- **THEN** SchedulerService SHALL log a warning with the skill name and error details
- **AND** the schedule SHALL NOT be created
- **AND** the skill SHALL still be usable for manual execution

#### Scenario: No schedule in SKILL.md

- **WHEN** a SKILL.md does not contain `metadata.mohist.schedule`
- **THEN** no schedule SHALL be created for that skill
- **AND** the skill remains available for manual execution only

### Requirement: Schedule persistence in SQLite

Schedules SHALL be persisted in the `agent_skill_schedules` table. Each row SHALL store the skill_id, schedule_type, schedule_value, anchor, next_run_at, last_run_at, enabled state, and timestamps.

#### Scenario: Persist a new schedule

- **WHEN** SchedulerService parses a valid schedule from a SKILL.md
- **THEN** a row SHALL be inserted into `agent_skill_schedules` with the parsed values
- **AND** `next_run_at` SHALL be computed and stored
- **AND** `enabled` SHALL default to 1

#### Scenario: Update schedule on SKILL.md change

- **WHEN** a SKILL.md schedule config changes (e.g. `every: "30m"` → `every: "1h"`)
- **THEN** the existing `agent_skill_schedules` row SHALL be updated with the new values
- **AND** `next_run_at` SHALL be recomputed from now
- **AND** any active timer for this schedule SHALL be reset

#### Scenario: Remove schedule when SKILL.md no longer has one

- **WHEN** a SKILL.md previously had a schedule but it is removed
- **THEN** the corresponding `agent_skill_schedules` row SHALL be deleted
- **AND** any active timer for this schedule SHALL be cancelled

### Requirement: Timer-based execution

SchedulerService SHALL use `setTimeout` to trigger skill execution at the computed `next_run_at`. When a timer fires, the SchedulerService SHALL invoke the existing SkillService execution path and compute the next `next_run_at`.

#### Scenario: Timer fires for every schedule

- **WHEN** the timer for a `{ every: "5m" }` schedule fires
- **THEN** SchedulerService SHALL invoke SkillService to execute the skill
- **AND** compute `next_run_at` as now + 5 minutes
- **AND** update `last_run_at` to now and `next_run_at` to the computed value in SQLite
- **AND** set a new timer for the computed `next_run_at`

#### Scenario: Timer fires for cron schedule

- **WHEN** the timer for a `{ cron: "0 9 * * 1-5" }` schedule fires
- **THEN** SchedulerService SHALL invoke SkillService to execute the skill
- **AND** compute `next_run_at` as the next cron match after now
- **AND** update `last_run_at` and `next_run_at` in SQLite
- **AND** set a new timer for the computed `next_run_at`

#### Scenario: Timer fires for one-time schedule

- **WHEN** the timer for a `{ at: "2026-05-01T10:00:00Z" }` schedule fires
- **THEN** SchedulerService SHALL invoke SkillService to execute the skill
- **AND** set `enabled = 0` for this schedule in SQLite
- **AND** NOT set a new timer

#### Scenario: Execution failure does not affect next schedule

- **WHEN** a scheduled execution fails (SkillService throws an error)
- **THEN** the failure SHALL be logged
- **AND** `last_run_at` SHALL be updated to now
- **AND** `next_run_at` SHALL be computed and stored as if execution succeeded
- **AND** a new timer SHALL be set for the computed `next_run_at`

### Requirement: Server restart recovery

On server startup, SchedulerService SHALL load all enabled schedules from SQLite, compute `next_run_at` for any schedule whose previous `next_run_at` is in the past, and trigger a catch-up execution for missed schedules.

#### Scenario: Restart with no missed executions

- **WHEN** server restarts
- **AND** all enabled schedules have `next_run_at` in the future
- **THEN** SchedulerService SHALL load all enabled schedules
- **AND** set timers for each `next_run_at` without modification

#### Scenario: Restart with missed execution

- **WHEN** server restarts
- **AND** an enabled schedule has `next_run_at` in the past
- **THEN** SchedulerService SHALL trigger a catch-up execution for that schedule immediately
- **AND** compute a new `next_run_at` from now
- **AND** update `next_run_at` in SQLite
- **AND** set a new timer for the computed `next_run_at`

#### Scenario: Restart with multiple missed executions

- **WHEN** server restarts
- **AND** an enabled schedule has `next_run_at` far in the past (e.g. server was down for days)
- **THEN** SchedulerService SHALL trigger at most ONE catch-up execution
- **AND** compute `next_run_at` from now (not from the missed time)
- **AND** update `next_run_at` in SQLite

#### Scenario: One-time schedule already past on restart

- **WHEN** server restarts
- **AND** an `at` schedule has `next_run_at` in the past
- **THEN** SchedulerService SHALL trigger the one-time execution
- **AND** set `enabled = 0` for this schedule

### Requirement: Concurrent schedule execution

When multiple schedules fire at the same time, SchedulerService SHALL respect the existing `maxConcurrentRuns` limit. Schedules that cannot run immediately SHALL be queued and executed when a slot becomes available.

#### Scenario: Multiple schedules fire simultaneously

- **WHEN** two schedules have the same `next_run_at`
- **AND** both timers fire
- **THEN** SchedulerService SHALL execute both up to `maxConcurrentRuns`
- **AND** any excess SHALL be queued and executed when a slot opens

#### Scenario: Schedule fires while manual execution is running

- **WHEN** a scheduled execution fires
- **AND** `maxConcurrentRuns` is already reached by manual executions
- **THEN** the scheduled execution SHALL be queued
- **AND** executed when a slot becomes available

### Requirement: DB schema migration for agent_skill_schedules

The system SHALL add an `agent_skill_schedules` table via a database migration to schema version 16.

#### Scenario: Fresh database gets agent_skill_schedules table

- **WHEN** a new database is initialized
- **THEN** the `agent_skill_schedules` table SHALL exist with columns: id (TEXT PK), skill_id (TEXT FK → agent_skills), schedule_type (TEXT), schedule_value (TEXT), anchor (TEXT), next_run_at (TEXT), last_run_at (TEXT), enabled (INTEGER DEFAULT 1), created_at (TEXT), updated_at (TEXT)

#### Scenario: Existing database migrates to v16

- **WHEN** an existing database at schema version 15 starts
- **THEN** migration SHALL add the `agent_skill_schedules` table
- **AND** update schema version to 16
