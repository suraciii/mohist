# Squashed Baseline: Accepted Deltas

Status: accepted

## Problem

The EF Core migration chain was squashed into one baseline migration. The
squash procedure in [`../db-migrations.md`](../db-migrations.md) is durable,
but every squash produces a point-in-time list of accepted differences between
upgraded and fresh databases. That list expires at the next squash, so it must
not live inside the durable procedure document.

## Decision

This record holds the point-in-time list of differences accepted when the
current baseline was verified. The procedure document stays durable, and the
next squash replaces this snapshot.

## Alternatives considered

**Keep the accepted-deltas list in the procedure document.** Rejected: the
procedure is durable while the list is replaced at every squash; mixing them
forces the durable contract to carry expiring content.

## Corrective upgrade bridge

The Workflow AgentJob migration briefly changed the already-published baseline from
`TaskRunId` to `ActionAttemptId`. Existing databases therefore may record the same baseline
id with either physical column shape. The retained-tail rename migration restores one
upgrade chain: the baseline creates `TaskRunId`, then the tail migration renames both
workflow artifact columns and their indexes. Before EF migration, startup recognizes only
the complete `ActionAttemptId` shape produced by the brief baseline revision and records the
rename migration as already satisfied. Missing tables, mixed columns, or incorrect index
shape fail startup instead of being guessed or rewritten.

## Accepted deltas at the current baseline

Schema equivalence was verified at squash time with these documented
differences; none is visible to EF or to Orleans at runtime:

- `AgentSubscriptions` and `RunnerWorks` exist in pre-squash databases (their
  drop migrations were dead). `20260911000000_DropVestigialTables` completes
  the drops on upgrade; the baseline never creates them.
- Renamed-table constraint names: SQLite `RENAME TABLE` does not rewrite
  inline constraint text, so upgraded databases keep `*ChildApp*`-era
  PK/FK/CHECK names while fresh databases use the model names. Constraint
  names carry no runtime semantics in SQLite.
- `SlackWorkspaceEnrollments.S2OriginalManagerTransportKind` remains as a dead
  column in upgraded databases; the model no longer declares it and the
  baseline omits it.
- `OrleansQuery.ClearStorageKey` text predates an in-place migration edit in
  databases created before that edit; the baseline carries the current text.
  Pre-existing drift, not introduced by the squash.
