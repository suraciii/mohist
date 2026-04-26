## Why

Issues lack a structured priority field, forcing users to rely on unstructured `priority:*` labels. This prevents priority-based sorting (high-priority issues sink to the bottom in number-ordered lists), priority-based filtering, and priority-aware scheduling when multiple issues compete for agent slots.

## What Changes

- Add `priority` TEXT column to `issues` table (values: `p0`–`p4`, default `p2`), via schema migration v14
- **BREAKING**: `Issue` type gains required `priority` field; `IssueQueryOptions` gains optional `priority` filter
- `GET /api/issues` accepts `?priority=p1` query parameter and defaults sort to priority ASC (then number ASC as tiebreaker)
- `PATCH /api/issues/:number` accepts `{ priority: "p0" | "p1" | "p2" | "p3" | "p4" }`
- `POST /api/issues` accepts optional `priority` field
- `mo issue create` gains `--priority <level>` flag
- `mo issue update <id>` gains `--priority <level>` flag
- `mo issue list` gains `--priority <level>` filter flag; default sort changes from number ASC to priority ASC, number ASC
- `mo issue list` and `mo issue show` display priority in output
- Migration extracts priority from existing `priority:*` labels (e.g., `priority:high` → `p1`) and removes the label

## Capabilities

### New Capabilities

- **issue-priority**: Priority field (p0–p4) on issues — storage, query, sort, CLI display, and label migration

### Modified Capabilities

- **local-issue-store**: `Issue` type adds `priority` field; `IssueQueryOptions` adds `priority` filter; `findAll` default sort changes to priority ASC, number ASC; `CreateIssueData` accepts `priority`
- **http-api**: `GET /api/issues` gains `?priority` query param and new default sort; `PATCH /api/issues/:number` accepts `priority`; `POST /api/issues` accepts `priority`
- **cli-interface**: `mo issue create` gains `--priority`; `mo issue update` gains `--priority`; `mo issue list` gains `--priority` filter and new sort order

## Impact

- **Database**: Schema migration v14 adds `priority` column + index on `issues(project_id, priority)`
- **Types**: `Issue` interface in `types/index.ts` gains `priority: Priority` field
- **IssueRepo**: `issue-repo.ts` — `IssueRow`, `rowToIssue`, `create`, `findAll`, `update` all touched
- **API routes**: Issue routes in `api/` — list handler (query + sort), create/update handlers
- **CLI commands**: `cli/commands/issue.ts` — create, update, list subcommands
- **Migration**: `migrations.ts` — v14 function migrates existing `priority:*` labels
