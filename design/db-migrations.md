# Database Migrations

The server persists to one SQLite database through EF Core migrations applied at startup.
Migration files accumulate without bound during active development; squashing collapses them.
This document defines the migration authoring contract and the squash procedure, so a squash
never silently breaks an existing database.

## Design Drivers

- Startup applies `DatabaseInitializer`, which runs EF migrations and then idempotent data
  upgraders. Self-hosted and development databases share this path; there is no separate
  migration tool.
- A squash deletes history. An existing database records old migration ids in
  `__EFMigrationsHistory`; a regenerated baseline is unknown to it and would re-execute
  `CREATE TABLE` against live tables. The upgrade path must therefore rewrite history
  explicitly, and must refuse databases it cannot safely rewrite.
- Hand-written migrations are common here (backfills, table rebuilds). EF Core discovers a
  migration only through `[Migration("id")]` plus `[DbContext(typeof(MohistDbContext))]`; a
  file missing either attribute is silently dead code. Two historical drop migrations
  (`20260718090000_DropAgentSubscriptions`, `20260730000000_DropRunnerWorksTable`) never ran
  for this reason. The authoring rule below exists so this cannot recur.

## Model

- **Baseline migration** — one migration that builds the complete schema of the squash floor
  from an empty database. It replaces every migration at or below the floor.
- **Squash floor** — the newest migration folded into the baseline
  (`SquashedMigrationHistory.FloorId`). Databases at or past the floor upgrade seamlessly;
  older databases do not.
- **Retained tail** — migrations newer than the floor stay untouched and continue to apply
  after the baseline.
- **History remap** — `SquashedMigrationHistory.RemapAsync` runs inside
  `DatabaseInitializer` before `MigrateAsync` and rewrites the history table of a pre-squash
  database to reference the baseline.

## Semantics

### Authoring rules

- A migration must carry both `[Migration("<id>_<Name>")]` and
  `[DbContext(typeof(MohistDbContext))]`. When a `.Designer.cs` pair exists it carries the
  attributes; a hand-written single-file migration must carry them itself.
- Migration ids are zero-padded timestamps (`yyyyMMddHHmmss`); ordering is lexicographic.
- Migrations are immutable once merged. Edit a migration only before it ships.

### Squash procedure

1. Pick the floor migration. Every supported database must be at or past it.
2. In a worktree at the commit where the floor is the newest migration, delete all
   migrations and the model snapshot, then scaffold one migration
   (`dotnet ef migrations add SquashedBaseline`). Rename it to sort immediately before the
   first retained migration.
3. Patch the baseline for content the EF model cannot express:
   - Non-model infrastructure owned by raw SQL (Orleans persistence/reminder tables and the
     `OrleansQuery` seed rows) must be copied verbatim.
   - Column `DEFAULT` constraints that historical `AddColumn` migrations needed (to add
     `NOT NULL` columns to live tables) but the model never declared must be re-applied, so
     a fresh database equals an upgraded one.
4. Delete the squashed migrations, their `.Designer.cs` pairs, and any helper used only by
   them. Delete the spec files that target deleted migrations.
5. Verify semantic equivalence before merging: dump `sqlite_master` (normalized to
   per-column/constraint fragments), `OrleansQuery` rows, and per-table row counts for the
   old chain and for baseline-plus-tail, on empty databases. The dumps must differ only in
   the documented deltas.

### History remap

`RemapAsync` classifies the database before EF runs:

- No history table, or an empty one: fresh database; nothing to rewrite.
- History already contains the baseline: remapped before; nothing to do.
- Newest applied migration below the floor: throw. The error names the database's newest
  migration and the floor, and instructs to first run a build that still carries the
  pre-squash chain.
- Otherwise: insert the baseline row (copying the newest recorded `ProductVersion`), then
  delete every row older than the first retained migration. The insert must precede the
  delete so the version copy has a source row.

### Current accepted deltas

Schema equivalence was verified at squash time with these documented differences; none is
visible to EF or to Orleans at runtime:

- `AgentSubscriptions` and `RunnerWorks` exist in pre-squash databases (their drop
  migrations were dead). `20260911000000_DropVestigialTables` completes the drops on
  upgrade; the baseline never creates them.
- Renamed-table constraint names: SQLite `RENAME TABLE` does not rewrite inline constraint
  text, so upgraded databases keep `*ChildApp*`-era PK/FK/CHECK names while fresh databases
  use the model names. Constraint names carry no runtime semantics in SQLite.
- `SlackWorkspaceEnrollments.S2OriginalManagerTransportKind` remains as a dead column in
  upgraded databases; the model no longer declares it and the baseline omits it.
- `OrleansQuery.ClearStorageKey` text predates an in-place migration edit in databases
  created before that edit; the baseline carries the current text. Pre-existing drift, not
  introduced by the squash.

## Status

- The remap floor is fixed at `20260906000000_AddWorkflowProfileAgentActionOverrides`. The
  next squash must move the floor forward and replace the baseline; the procedure above
  applies unchanged.
