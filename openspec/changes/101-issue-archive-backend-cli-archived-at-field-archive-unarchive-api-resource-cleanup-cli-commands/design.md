## Context

mohist tracks issues through a pipeline (backlog → plan → build → check → done) with no mechanism to retire completed issues. The `IssueRepo.findAll()` returns all issues unconditionally, `mo issue list` shows everything, and worktree/openspec directories persist indefinitely. The codebase has three layers we need to touch: DB (`issue-repo.ts`, `migrations.ts`), Service (`issue-service.ts`), and API/CLI (`api/issues.ts`, `cli/commands/issue.ts`).

An existing `ChangeArtifactsManager.archiveChange(issueNumber)` in `artifacts/change-artifacts-manager.ts` already moves `openspec/changes/{N}-slug/` → `openspec/changes/archive/{N}-slug/`, and `WorktreeManager.remove()` already handles worktree + branch cleanup. We reuse both.

Current schema version is 15. Migration pattern: single `migrations.ts` file, `migrateToVersionN(db)` functions in a transaction, column existence checks via `PRAGMA table_info`.

## Goals / Non-Goals

**Goals:**
- Add `archived_at` field to issues table with migration
- Make `findAll()` default-exclude archived issues (zero impact on existing callers)
- Add archive/unarchive service methods with resource cleanup orchestration
- Add 3 API endpoints and 2 CLI subcommands
- Reuse existing `ChangeArtifactsManager` and `WorktreeManager` for resource cleanup

**Non-Goals:**
- Kanban UI integration (Issue #102)
- Auto-archive / scheduled archival
- Separate archive list page
- Archiving from the `Close` endpoint (archive is a separate action)

## Decisions

### D1: Use `archived_at TEXT` column instead of a new status value

Add `archived_at TEXT DEFAULT NULL` to the `issues` table. Non-NULL means archived.

**Why:** An `archived_at` timestamp is strictly more useful than a boolean — it tells you *when*. It avoids coupling archive state with the `status` enum (which has workflow semantics: active/completed/blocked/paused/interrupted). An archived issue preserves its original stage and status.

**Alternatives considered:**
- `IssueStatus.Archived` enum value — would conflate lifecycle state with view-filtering state; breaks existing stage/status assumptions
- Separate `archived_issues` table — overkill for SQLite; complicates unarchive and cross-referencing

### D2: `findAll()` filtering via `IssueQueryOptions` extension, not separate method

Extend `IssueQueryOptions` with `{ includeArchived?: boolean; archivedOnly?: boolean }`. `findAll()` adds `AND (archived_at IS NULL OR :includeArchived = true)` to the WHERE clause when appropriate.

**Why:** All existing callers of `findAll()` pass an options object. Default behavior (`includeArchived=false`, `archivedOnly=false`) automatically excludes archived issues without touching call sites. The `findArchived()` dedicated method exists for convenience but delegates to `findAll({ archivedOnly: true })`.

**Alternatives considered:**
- Separate `findActive()` / `findArchived()` methods only — duplicates query logic, drift risk
- Always return everything, filter at API layer — defeats the purpose, every new caller must remember to filter

### D3: Reuse `ChangeArtifactsManager.archiveChange()` for openspec archival

The service layer calls `ChangeArtifactsManager.archiveChange(issueNumber)` directly. For unarchive, calls `restoreChange(issueNumber)`. Both methods already handle the directory move and throw `ChangeNotFoundError` if the directory doesn't exist.

**Why:** Avoids duplicating filesystem logic. The existing implementation already handles: finding the change dir by issue number prefix, creating the archive directory, and `fs.renameSync` for the move.

**Catch:** The existing `archiveChange` does NOT prepend a date prefix — it moves `{N}-slug/` to `archive/{N}-slug/`. The spec says `archive/YYYY-MM-DD-slug/`. We'll extend `archiveChange` to accept an optional `prefixDate` parameter, or handle the rename at the service layer after calling `archiveChange`.

**Decision:** Call `ChangeArtifactsManager.archiveChange()` as-is (no date prefix), then rename the archived directory to add the date prefix in the service. This avoids modifying the existing method's contract. If the rename fails (unlikely), the archive still succeeded — just without the date prefix.

### D4: Archive guards — check for running agent, not stage/status

The service checks if the issue has an active agent session (via `AgentRunner` or equivalent) before archiving. It does NOT restrict by stage — you can archive an issue at any stage (even backlog). However, archiving a non-done issue returns a warning string alongside the success result.

**Why:** Users may want to archive abandoned issues in any state. The running-agent check is the real safety gate — it prevents data loss during active work.

**Alternatives considered:**
- Only allow archiving `stage=done` — too restrictive, doesn't handle abandoned issues
- Only allow archiving `status=completed` — same problem

### D5: `GET /api/issues` breaking change handled via `?all=true` escape hatch

The default behavior of `GET /api/issues` changes from "return all" to "exclude archived". Callers that need the old behavior use `?all=true`.

**Why:** This is the correct default — most consumers want active issues. The `?all=true` param preserves backward compatibility for any existing scripts or integrations.

**Risk mitigation:** The change is transparent to CLI users (the CLI always used the API). The web UI (Issue #102) will be updated simultaneously.

### D6: Batch archive is sequential, not parallel

`archiveAllCompleted()` iterates issues one-by-one, calling the single `archive()` method for each. No `Promise.all`.

**Why:** Filesystem operations (worktree remove, directory rename) and git operations are I/O-bound and can interfere with each other. Sequential is simpler and avoids race conditions on shared git state. The number of completed issues at any given time is typically small (< 50).

## Risks / Trade-offs

- **[Breaking change: GET /api/issues]** → Mitigated by `?all=true` param. Document in CHANGELOG. Web UI updated in same release cycle (#102).
- **[Unarchive doesn't restore worktree]** → By design. Worktree is recreated on `mo issue start`. Avoids disk waste from restoring potentially stale branches.
- **[Migration adds column to large tables]** → SQLite `ALTER TABLE ADD COLUMN` is O(1) for existing rows. No risk.
- **[Race: archive while agent finishes]** → Guard checks for running agent. If agent completes between check and archive, the archive succeeds safely (issue is already done).
- **[Archive fails mid-cleanup (e.g., worktree removed but openspec not archived)]** → Archive is idempotent. Re-running sets `archived_at` again (no-op) and retries cleanup steps. Already-completed cleanup steps are skipped gracefully (worktree not found → skip, etc.).

## Migration Plan

1. **Migration v16**: `ALTER TABLE issues ADD COLUMN archived_at TEXT DEFAULT NULL` + `CREATE INDEX IF NOT EXISTS idx_issues_archived ON issues(archived_at)`
2. **IssueRepo**: Add `archive()`, `unarchive()`, extend `findAll()` with filter options. All existing callers unaffected (default excludes archived).
3. **IssueService**: Add `archive()`, `unarchive()`, `archiveAllCompleted()`. Wire to `WorktreeManager.remove()` and `ChangeArtifactsManager.archiveChange()`.
4. **API**: Add 3 new routes. Modify `GET /` handler to respect `archived`/`all` query params.
5. **CLI**: Add `archive` and `unarchive` subcommands. Add `--archived` and `--all` flags to `list`.
6. **No rollback needed** — `archived_at DEFAULT NULL` means existing data is unaffected. If the feature is reverted, the column is ignored.

## Open Questions

- Should `mo issue show <archived-number>` work without special flags? (Assumed yes — show always returns by number regardless of archive state.)
