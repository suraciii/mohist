## Context

`AgentRunnerService.recoverIssues()` currently treats all non-awaiting active issues identically: set to `blocked`, clear approval state. The ACP session exit handlers in `acp-session.ts` only write workflow logs when the process dies during initialization (`!initialized`), so running-phase crashes leave no trace.

`AgentRunnerService` has access to `IssueRepo` but not `ProjectRepo` or `WorktreeManager`. To read tasks.json, it needs to resolve an issue → project → worktree path → change directory → tasks.json.

## Goals / Non-Goals

**Goals:**
- ACP process exits logged unconditionally with phase (init/running) in both `runAcpSession` and `createAcpConnection`
- Build-stage orphans with all tasks passing auto-advance to review instead of being blocked
- Build-stage orphans with partial progress get blocked with a descriptive log message

**Non-Goals:**
- Plan/review/explore stage smart recovery (too ambiguous to determine progress)
- Resuming a partially completed build automatically after recovery (user must `reopen` + `start`)
- Persisting a "recovered" flag on the issue (not needed — tasks.json is the source of truth)

## Decisions

### D1: Inject ProjectRepo + WorktreeManager into AgentRunnerService

Add `ProjectRepo` and `WorktreeManager` as optional constructor parameters. `recoverIssues()` uses them to resolve worktree paths and read tasks.json. Both are optional — if absent, recovery falls back to the current blind-block behavior.

**Why:** Avoids adding a new abstraction. The server/index.ts already creates both objects right before constructing `AgentRunnerService`. No new interface needed.

**Alternatives considered:**
- *Callback function `resolveWorktreePath(issue)`*: More flexible but adds indirection for no clear benefit.
- *Reading tasks.json from workflow_log instead of filesystem*: workflow_log doesn't store task pass/fail status, only event logs. tasks.json is the authoritative source.

### D2: Reuse existing detector functions for change directory resolution

Use `findChangeDir(worktreePath, issueNumber)` from `openspec/detector.ts` to locate the change directory, then `fs.readFileSync` + `JSON.parse` for tasks.json. No new utility functions.

**Why:** `findChangeDir` already handles versioned change dirs (e.g. `23-fix-foo-v2`). `detectOpenSpecChange` requires tasks.json to exist — we don't want that constraint (we want to distinguish "no change dir" from "change dir exists but no tasks.json").

### D3: Move writeSessionLog outside the init guard in both exit handlers

In both `runAcpSession` (~L139) and `createAcpConnection` (~L515), the `writeSessionLog` call moves from inside `if (!initialized && code !== 0)` to before it. The log now always fires, with a `phase` field added. The existing `rejectOnSpawn`/`rejectOnInit` logic stays inside the guard unchanged.

**Why:** Minimal diff. The two separate `proc.on('exit')` handlers (one for stream cleanup, one for exit logic) are preserved as-is.

**Alternatives considered:**
- *Merge the two exit handlers into one*: Would work but increases diff size and risk.

### D4: No schema or API changes for reopen-resume

The reopen-resume spec requires distinguishing review-stage issues that were auto-advanced by recovery. Instead of adding a flag field, the reopen logic will check: if `stage=review` AND `status=blocked` AND tasks.json exists with all tasks passing → keep review stage on reopen. This is a runtime check, no DB migration.

**Why:** tasks.json is already the source of truth for build completion. Adding a flag would require a schema migration for marginal benefit.

## Risks / Trade-offs

- [Race: tasks.json read during concurrent write] → Mitigation: `recoverIssues()` runs at server startup before any agents start. No concurrent writes possible.
- [Stale tasks.json after manual edits] → Mitigation: If someone manually edited tasks.json incorrectly, the fallback is blocked — same as current behavior.
- [Worktree deleted before server restart] → Mitigation: `findChangeDir` returns null → fallback to blocked. Safe degradation.

## Migration Plan

No migration needed. Changes are backward-compatible:
- New constructor parameters are optional
- Existing behavior preserved when ProjectRepo/WorktreeManager are absent
- No schema changes

## Open Questions

None.
