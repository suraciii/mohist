## Context

`mohist/archive-change` (`packages/runner/src/actions/openspec.ts:221`) is the runner
action that moves a completed OpenSpec change directory into
`${dirname(changeDir)}/archive/<archiveName>` and commits the move. To make the
move idempotent across retries, it persists the computed archive name to a
run-scoped workflow runtime variable via `context.writeVars` (contract:
`packages/runner/src/core/types.ts:168` — best-effort, immediate, **not** rolled
back on later failure, so retries can observe the value).

Today that variable is the nested map `_actions.archiveChange.destination`, keyed
by the change directory's path relative to `workDir` (`sourceRel`). Two problems:

1. **Cross-date retry dies with `missing-source`.** When the persist is missing
   (run started before persist existed, or persist failed mid-flight) and the
   retry crosses a UTC date boundary, the action recomputes
   `${today}-${sourceName}` at `openspec.ts:231`. The source has already moved,
   the old-date archive exists, the new-date archive does not, so the
   `!sourceHasFiles` branch at `openspec.ts:292` finds nothing and returns
   `missing-source`, blocking integrate.
2. **The map shape is overspecified.** One workflow run archives exactly one
   OpenSpec change directory (`proposal.md` Non-Goals), so the
   `[changeRel]` indexing adds reading/writing complexity for no capability.

There is also a **backfill gap** in the existing `!sourceHasFiles` branch
(`openspec.ts:293`): when an existing archive is found by `findExistingArchive`,
the action sets `destination` and continues, but never persists the name — so the
very next same-run retry recomputes the prefix and can fail as in (1).

The `persist-name` failure stage and `retry-safe` error code already exist for
the first-write persist path; this design reuses them.

No DB schema, workflow YAML, or server change is involved — this is a runner
action runtime-variable contract change confined to `archiveChangeAction` and its
tests. Placement per `design/architecture.md`: OpenSpec file side effects and
runner runtime variables belong to the Execution Plane (Runner).

## Goals / Non-Goals

**Goals:**

- Replace the nested-map checkpoint with a single run-scoped string variable
  `openspecArchiveName` (archive directory basename, e.g.
  `2026-06-27-issue-276`).
- Persist `openspecArchiveName` **before** the directory move and reuse it on
  every retry/rerun, eliminating date-prefix recomputation across retry
  boundaries.
- Close the backfill gap: when the source is gone, no `openspecArchiveName` is
  set, but an existing archive is found, backfill `openspecArchiveName` from
  that archive's basename before continuing.
- Keep reading the legacy `_actions.archiveChange.destination[changeRel]` for
  in-flight runs, with best-effort migration to `openspecArchiveName`.
- Preserve the existing retry-safe `persist-name` failure semantics for both
  first-write and backfill persist failures.

**Non-Goals (per proposal):**

- Do not change the archive directory naming format (`YYYY-MM-DD-${sourceName}`
  and its `-vN` variants stay).
- Do not redesign workflow profile variables or touch `mohist/github-pr` /
  `mohist/default` YAML — the variable is internal to the action.
- Do not support multiple OpenSpec change directories in one workflow run.
- Do not mutate run variables outside this action's run profile.

## Decisions

### D1. New constant, single string variable

Introduce `OPENSPEC_ARCHIVE_NAME_VAR_KEY = "openspecArchiveName"`. The value is
the archive directory **basename** only — never an absolute path, never a map.
The archive is always located at `${dirname(changeDir)}/archive/${name}`, which
is already how the action resolves destinations today via
`resolveArchiveDestination` (`openspec.ts:526`).

**Alternatives considered:**

- _Keep the map, just add the backfill persist._ Rejected: the issue explicitly
  asks for the simpler contract, and the map buys nothing for a
  single-change-per-run model.
- _Store the absolute archive path._ Rejected: brittle if the workspace root
  ever moves; basename + `dirname(changeDir)` is portable and matches the
  existing resolution helper.
- _Store both keys on write during a transition._ Rejected: the spec requires
  the legacy key be read-only for new runs (spec.md:69). Writing both doubles
  the write surface and the migration story; the read-side fallback below is
  sufficient for compatibility.

### D2. Read order: new key first, legacy fallback, then recompute

Resolve the effective archive name in this priority:

1. `variables["openspecArchiveName"]` if it is a non-empty string.
2. Else `variables["_actions.archiveChange.destination"][sourceRel]` if it is a
   non-empty string (legacy, best-effort).
3. Else `null` → compute `${today}-${sourceName}` for a first-time run.

`sourceRel` (`relativePath(workDir, changeDir)`, `openspec.ts:557`) is retained
**only** for step 2; it is no longer used for writing. The legacy read is
defensive: malformed shape → treat as absent (mirrors the existing
`archiveDestinationMap` helper at `openspec.ts:14`).

### D3. Write only `openspecArchiveName`, at two points

Both write sites use the same shape `{ openspecArchiveName: basename }`:

- **Before move** (replaces `openspec.ts:316`): once the resolved archive name
  is known for a first-time or versioned move, persist before `moveChangeDir`.
- **Backfill continuation** (new, in the `!sourceHasFiles` branch around
  `openspec.ts:293`): when `findExistingArchive` returns a directory and no
  effective name was read, set `openspecArchiveName = basename(existingArchive)`
  and persist before treating it as the destination.

**Migration:** when step 2 of D2 supplied the name (legacy only), the same
before-move / backfill write naturally migrates it to `openspecArchiveName`. The
legacy key is never written and never deleted; it simply becomes stale, which is
harmless because step 1 takes precedence on every subsequent read.

Both writes flow through `context.writeVars` → `patchRunVars`
(`runtime/executor.ts:580`); no new wiring.

### D4. Reuse `persist-name` retry-safe failure for both write sites

A `writeVars` rejection at either write site returns `{ errorCode: "retry-safe",
stage: "persist-name" }` and does **not** move the source. This matches the
existing first-write behavior (`openspec.ts:319`) and the spec scenario
"Backfill persist failure is retry-safe" (spec.md:61). Retry re-attempts the
persist; because `writeVars` is idempotent (last writer wins with the same
basename), a partial earlier write is not a problem.

### D5. Validate every resolved name

`validateArchivePrefix` (`openspec.ts:494`) runs on the effective name from D2
**and** on the backfilled basename, exactly as today. This preserves the
existing unsafe-name rejection (test: `ArchiveChangeRejectsUnsafePersistedName_*`
at `openspec.spec.ts:1165`) for both the new key and the legacy fallback.

## Risks / Trade-offs

- **[In-flight run rolled back to old runner]** A run that started on the new
  runner and persisted only `openspecArchiveName`, then is retried after a
  rollback to the old runner, will not find the legacy key and will recompute
  the date prefix — re-exposing the original cross-date bug for that run.
  -> Mitigation: do not roll back the runner mid in-flight `integrate` runs;
  let them drain. The old runner remains forward-compatible with runs it
  started (it writes the legacy key itself).
- **[Legacy key drift]** After migration the legacy map entry is stale but never
  cleaned up. -> Mitigation: step 1 of D2 always wins, so stale legacy data is
  inert. Garbage-collecting it is out of scope (would require a write to a key
  we've declared read-only).
- **[Concurrent retries]** Two retries of the same task could race on
  `writeVars`. -> Mitigation: both write the same basename for the same run, so
  last-write-wins is convergent. The action's other retry hazards (git index
  races) are pre-existing and unchanged.
- **[Backfill picks the wrong archive]** `findExistingArchive` may match a
  `-vN` sibling if the base name is taken. -> Mitigation: backfill persists the
  **actual matched** basename, so the same archive is reused on every subsequent
  retry; no re-search after the first backfill.
- **[Map shape was externally observable]** The nested map was an internal
  implementation detail, not a documented public variable, and no profile or
  external consumer reads it. -> Mitigation: none needed; confirmed by grep
  (only `openspec.ts` and its own tests reference the key).

## Migration Plan

1. **Code** (`packages/runner/src/actions/openspec.ts`): add the new constant,
   replace the read path with D2's priority order, change both write sites to
   the single-key shape (D3), add the backfill persist in the
   `!sourceHasFiles` branch, keep `validateArchivePrefix` on all resolved names
   (D5). Drop `archiveDestinationMap`'s write usage but keep its read shape for
   step 2.
2. **Tests** (`packages/runner/tests/openspec.spec.ts`):
   - Update `ArchiveChangePersistsArchiveNameBeforeMove` (line 1012) and
     `ArchiveChangeRetryAfterVersionedMove_…` (line 1058) to assert the new
     `{ openspecArchiveName }` payload.
   - Update `ArchiveChangeCrossDayRetry_…` (line 1129) and the unsafe-name
     parametrized test (line 1165) to seed the new key (and add a variant that
     seeds the legacy key to prove fallback).
   - Add: existing-archive backfills `openspecArchiveName` before continuing;
     subsequent same-run cross-date retry reuses the backfilled name; backfill
     persist failure returns `persist-name` retry-safe; legacy-only run migrates
     to `openspecArchiveName` on first write.
3. **Verify:** `npm run typecheck -w packages/runner` then
   `npm test -w packages/runner`.
4. **Deploy:** `mo update runner` (per `AGENTS.md`, do not `dotnet run` —
   avoids runner id drift). Server and web are unchanged; no schema migration.
5. **Rollback:** revert runner and `mo update runner` again. Safe for runs
   started under the old runner; for runs started under the new runner, let the
   in-flight `integrate` stage drain before rolling back (see Risks).

## Open Questions

- None outstanding. The variable contract, read/write precedence, backfill
  trigger, and failure semantics are fully specified by
  `specs/archive-change-idempotency/spec.md`. Concurrency and GC are settled by
  D3/D4 and the Risks section.
