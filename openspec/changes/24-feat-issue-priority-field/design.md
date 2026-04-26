## Context

Current schema version is 13. Issues have no `priority` column — `IssueRepo.findAll()` sorts by `number ASC`, `IssueService` has no priority concept, API and CLI have no priority parameters. Some users work around this via `priority:*` labels, which are unstructured and not queryable.

The codebase follows a layered pattern: `types/` → `db/repo` → `services/` → `api/` → `cli/`. Changes propagate top-down through all layers.

## Goals / Non-Goals

**Goals:**
- Add `priority` field (p0–p4) to `Issue` type, database, repo, service, API, and CLI
- Migration v14 that adds the column and extracts priority from existing `priority:*` labels
- Default sort changes from `number ASC` to `priority ASC, number ASC`
- Priority validation at API layer (400 for invalid values)

**Non-Goals:**
- Priority-aware agent scheduling (Phase 2, depends on #7 pause/stop API)
- Runtime priority auto-adjustment
- WebUI changes (separate issue)

## Decisions

### D1: Priority stored as TEXT column, not integer

Store `priority` as TEXT (`'p0'`–`'p4'`) rather than INTEGER (0–4). SQLite sorts TEXT lexicographically, and `'p0' < 'p1' < ... < 'p4'` is correct. This avoids a type conversion layer and keeps the values human-readable in queries and API responses.

**Alternatives considered:** INTEGER column with 0–4 values. Would sort identically but requires mapping at every API/CLI boundary and is less self-documenting in raw SQL.

### D2: Validation at API layer, not repo layer

Priority validation (`isValidPriority()`) happens in the API route handlers (issues.ts). `IssueRepo` trusts callers — consistent with existing pattern where repo doesn't validate `stage` or `status` values.

**Alternatives considered:** Validation in `IssueService`. Rejected because service is also called internally (agent runner, workflow controller) where we trust the inputs. API is the trust boundary.

### D3: Label migration uses explicit mapping table

Migration v14 iterates all issues, checks labels for `priority:*` prefix, maps via a fixed lookup table (`priority:critical`→`p0`, `priority:high`→`p1`, etc.), sets the `priority` column, and removes matched labels from the JSON array. Issues without `priority:*` labels get the column DEFAULT `'p2'`.

### D4: Default sort change is global

`IssueRepo.findAll()` changes from `ORDER BY number ASC` to `ORDER BY priority ASC, number ASC`. This affects all callers (API list, service queries). No per-call sort option is added — if needed later, it can be added to `IssueQueryOptions`.

### D5: Priority type as string literal union

Add `type Priority = 'p0' | 'p1' | 'p2' | 'p3' | 'p4'` and `const VALID_PRIORITIES` array in `types/index.ts`. The `Issue` interface gains `priority: Priority`.

## Risks / Trade-offs

- **[Breaking sort order]** → Existing `mo issue list` output order changes. All p2 issues (default) retain relative order by number, so the visible change is minimal for users without explicit priorities.
- **[Label migration only runs once]** → If users add `priority:*` labels after migration, they won't auto-convert. Acceptable — the label convention is deprecated in favor of the structured field.
- **[No down migration]** → SQLite `ALTER TABLE ADD COLUMN` is irreversible without table rebuild. Low risk — adding a column with a default is non-destructive.

## Migration Plan

1. Bump `SCHEMA_VERSION` from 13 to 14 in `migrations.ts`
2. Add `migrateToVersion14()` that:
   - Checks if `priority` column exists (same guard pattern as v9)
   - `ALTER TABLE issues ADD COLUMN priority TEXT NOT NULL DEFAULT 'p2'`
   - Creates index `idx_issues_project_priority`
   - Iterates all issues: for each, parses labels JSON, extracts `priority:*` label, maps to priority value, updates `priority` column and removes the label from the JSON array
   - Sets schema version to 14
3. Add `if (currentVersion < 14) { migrateToVersion14(db); }` to `initializeDatabase`
4. No rollback — column addition is additive

## Open Questions

None.
