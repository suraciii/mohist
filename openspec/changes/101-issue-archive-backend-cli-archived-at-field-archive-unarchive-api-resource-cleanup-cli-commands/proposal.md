## Why

Completed issues accumulate indefinitely in the issues table, polluting `mo issue list` output and leaving worktree/openspec directories on disk. There is currently no mechanism to move finished work out of the active view, making it harder to focus on in-progress work and wasting disk space as the number of completed issues grows.

## What Changes

- DB: Add `archived_at TEXT DEFAULT NULL` column to issues table via migration
- IssueRepo: Add `archive()`, `unarchive()`, `findArchived()` methods; modify `findAll()` to default-exclude archived issues with `includeArchived` / `archivedOnly` filter options
- IssueService: Add `archive()`, `unarchive()`, `archiveAllCompleted()` methods that coordinate status marking with resource cleanup (worktree removal, openspec change archival, checkpoint cleanup)
- **BREAKING** `GET /api/issues`: Default behavior changes from "return all issues" to "exclude archived issues"; add `?archived=true`, `?all=true` query params
- New API endpoints: `POST /api/issues/:number/archive`, `POST /api/issues/:number/unarchive`, `POST /api/issues/archive-completed`
- CLI: Add `mo issue archive <number>`, `mo issue archive --all-completed`, `mo issue archive --no-cleanup`, `mo issue unarchive <number>` commands
- CLI: Add `--archived` and `--all` flags to `mo issue list`
- Resource cleanup on archive: git worktree removal, openspec change directory archival, pipeline checkpoint cleanup
- Guard: Prevent archiving issues with running agents

## Capabilities

### New Capabilities

- `issue-archive` — Archival lifecycle for issues: marking archived, resource cleanup (worktree + openspec), unarchive, batch archive completed issues

### Modified Capabilities

- `local-issue-store` — `findAll()` gains archived filtering semantics (default exclude, includeArchived, archivedOnly options)
- `http-api` — Issues list endpoint changes default behavior and adds archive/unarchive endpoints
- `cli-interface` — New `archive`/`unarchive` subcommands and `--archived`/`--all` list flags
- `worktree-manager` — Add cleanup method for archive flow (remove worktree + branch)

## Impact

- **DB layer**: New migration; `IssueRepo.findAll()` query changes affect all issue listing paths
- **Service layer**: `IssueService` grows 3 new public methods; resource cleanup touches `WorktreeManager` and the openspec change archival mechanism
- **API layer**: `GET /api/issues` is a **breaking change** for any consumer relying on seeing all issues by default
- **CLI layer**: New subcommands under `mo issue`; new flags on `mo issue list`
- **Disk**: Archived issues release worktree disk space; openspec changes move to `openspec/changes/archive/`
