# Squashed Baseline: Accepted Deltas

## Background

The EF Core migration chain was squashed into one baseline migration.
[`../db-migrations.md`](../db-migrations.md) defines the authoring contract and the squash
procedure. This record holds the point-in-time list of differences accepted when the current
baseline was verified, so the procedure document stays durable while the snapshot can be replaced
by the next squash.

## Accepted deltas at the current baseline

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
