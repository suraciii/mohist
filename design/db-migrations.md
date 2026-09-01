# Database Migrations

The Server persists to one SQLite database through EF Core migrations applied
at startup. `DatabaseInitializer` applies migrations and then idempotent data
upgraders for self-hosted and development databases. This spec defines
migration authoring and squash rules so an existing database is never silently
broken.

## Design Drivers

- EF discovers a migration only through both required attributes. A missing
  attribute makes hand-written migration code dead without an obvious failure.
- A squash deletes migration history. A regenerated baseline is unknown to an
  existing database and would re-run `CREATE TABLE` against live tables.
- The upgrade path must rewrite `__EFMigrationsHistory` explicitly and must
  refuse databases that it cannot safely rewrite.
- Hand-written migrations carry backfills and table rebuilds. The authoring
  contract must protect those operations.

## Model

- **Baseline migration**: one migration that builds the complete schema at the
  squash floor from an empty database. It replaces every migration at or below
  the floor.
- **Squash floor**: the newest migration folded into the baseline,
  `SquashedMigrationHistory.FloorId`.
- **Retained tail**: migrations newer than the floor. They remain unchanged and
  apply after the baseline.
- **History remap**: `SquashedMigrationHistory.RemapAsync` runs in
  `DatabaseInitializer` before `MigrateAsync`. It rewrites pre-squash history
  to reference the baseline.

## Semantics

### Authoring rules

- A migration must carry both `[Migration("<id>_<Name>")]` and
  `[DbContext(typeof(MohistDbContext))]`. A `.Designer.cs` pair carries the
  attributes. A hand-written single-file migration carries them itself.
- Migration IDs use zero-padded timestamps in `yyyyMMddHHmmss` format. Ordering
  is lexicographic.
- Migrations are immutable after merge. Edit one only before it ships.
- A persisted table or column rename after the baseline ships requires a new
  retained-tail migration. Editing only the baseline or model snapshot fixes
  fresh databases but not existing rows. The incremental migration must
  preserve rows and recreate every affected index.

### Squash procedure

1. Pick the floor migration. Every supported database must be at or past it.
2. In a worktree where the floor is newest, delete all migrations and the model
   snapshot. Scaffold one migration with
   `dotnet ef migrations add SquashedBaseline`. Rename it to sort immediately
   before the first retained migration.
3. Patch the baseline for content that the EF model cannot express:
   - Copy non-model infrastructure owned by raw SQL verbatim, including
     Orleans persistence and reminder tables and `OrleansQuery` seed rows.
   - Reapply column `DEFAULT` constraints that historical `AddColumn`
     migrations needed for `NOT NULL` columns on live tables but the model did
     not declare. A fresh database must equal an upgraded database.
4. Delete the squashed migrations, their `.Designer.cs` pairs, and helpers used
   only by them. Delete spec files that target deleted migrations.
5. Verify semantic equivalence before merging. On empty databases, dump
   normalized `sqlite_master` fragments per column and constraint,
   `OrleansQuery` rows, and per-table row counts for the old chain and for
   baseline plus tail. Differences must be limited to documented deltas.

### History remap

`RemapAsync` classifies the database before EF runs:

```text diagram
                               +---------------+
                               | Open database |
                               +-------+-------+
                                       |
                                       v
                              +----------------+
                              | History table? |
                              +--------+-------+
         +-----------------+-----------+--------+-------------------------+
         vnone or empty    vbaseline present    vnewest below floor       vnewest at or above floor
+----------------+   +----------+    +---------------------+   +---------------------+
| Fresh database |   | No remap |    | Throw with guidance |   | Insert baseline row |
+----------------+   +----------+    +---------------------+   +----------+----------+
                                                                          |
                                                                          v
                                                             +-------------------------+
                                                             | Delete old history rows |
                                                             +-------------------------+
```

- No history table or an empty table means a fresh database. Nothing is
  rewritten.
- History that already contains the baseline was remapped. Nothing is done.
- If the newest applied migration is below the floor, throw. The error names
  the newest migration and floor and tells the operator to run a build that
  still carries the pre-squash chain first.
- Otherwise, insert the baseline row with the newest recorded
  `ProductVersion`, then delete every row older than the first retained
  migration. Insert first so the version copy has a source row.

### Current accepted deltas

The current squash baseline's accepted schema deltas are recorded in
[`decisions/squashed-baseline.md`](decisions/squashed-baseline.md).

## Status

`20260906000000_AddWorkflowProfileAgentActionOverrides` is the current remap
floor. No later squash has replaced the baseline.
