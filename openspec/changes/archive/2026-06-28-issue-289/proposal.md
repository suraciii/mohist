## Why

`mohist/archive-change` persists its archive directory name in the run variable
`_actions.archiveChange.destination`, a map keyed by the change directory's
relative path. When that write is missing (e.g. a run that started before the
persist step existed, or a persist that failed mid-flight), a cross-UTC-date
retry recomputes the archive prefix from today's date: the source directory has
already moved, the old-date archive exists, the new-date archive does not, and
the retry dies with `missing-source`, blocking integrate. The map keying is also
unnecessary — one workflow run only ever archives a single OpenSpec change
directory — so the variable contract is harder to read and reason about than it
needs to be.

## What Changes

- Replace the `_actions.archiveChange.destination[changeRel]` map with a single
  run-scoped string variable **`openspecArchiveName`** holding the archive
  directory basename (e.g. `2026-06-27-issue-276`). The runner action locates
  the archive at `${dirname(changeDir)}/archive/${openspecArchiveName}`.
- `archiveChangeAction` writes `openspecArchiveName` **before** moving
  `changeDir` into the archive, and reuses it on every retry/rerun instead of
  recomputing the date prefix.
- **Backfill gap closed**: when the source directory is gone, no
  `openspecArchiveName` is present, but an existing archive matching the current
  prefix is found, the action backfills `openspecArchiveName` from that existing
  archive's basename before continuing — so a later same-run cross-date retry
  no longer recomputes.
- **Compatibility**: `_actions.archiveChange.destination[changeRel]` remains
  readable for in-flight runs; when only the legacy key is present the action
  best-effort migrates it to `openspecArchiveName` so subsequent retries use the
  new key.
- A persist failure before the move (or before backfill continuation) returns a
  retry-safe failure with a clear `persist-name` stage.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `archive-change-idempotency`: The archive-name checkpoint variable contract
  changes from the nested map `_actions.archiveChange.destination[changeRel]`
  to the single string `openspecArchiveName` (basename, run-scoped). A new
  requirement is added: when the source is missing and an existing archive is
  found without `openspecArchiveName`, the action backfills the variable before
  continuing. A new requirement is added for legacy-key read compatibility and
  best-effort migration to `openspecArchiveName`. The existing
  mid-execution-write requirement is unchanged (same `writeVars` path, just a
  different key shape).

## Impact

- **Runner** (`packages/runner/src/actions/openspec.ts`): `archiveChangeAction`
  read/write paths switch to `openspecArchiveName`; add legacy-key fallback,
  migration, and existing-archive backfill; keep the `persist-name` failure
  stage.
- **Runner tests** (`packages/runner/tests/openspec.spec.ts`): add coverage for
  before-move persistence with the simple key, existing-archive backfill,
  cross-date retry using `openspecArchiveName`, and legacy-key fallback /
  migration; update existing tests that assert the map shape.
- **Workflow profiles** (`mohist/github-pr`, `mohist/default`): no YAML change —
  the variable is internal to the action; both profiles inherit the fix.
- **No database schema migration**; the change is confined to the runner action
  runtime-variable contract.
