# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

_None._

## Review Evidence

- Issue acceptance criteria were reviewed from `mo issue show 289 --project-id proj_f6c141d63b6243bfbb481737b2243b87`. The candidate implements the requested runner-only archive checkpoint change: `OPENSPEC_ARCHIVE_NAME_VAR_KEY = "openspecArchiveName"` is defined in `packages/runner/src/actions/openspec.ts:12`, and writes now go through `persistArchiveName()` with `{ openspecArchiveName: archiveName }` at `packages/runner/src/actions/openspec.ts:462`.
- AC1 and AC2 are satisfied: `archiveChangeAction` resolves the effective name from `openspecArchiveName` before legacy or computed names at `packages/runner/src/actions/openspec.ts:22`, and the first-time/versioned move path persists `resolvedArchiveName` before `moveChangeDir()` at `packages/runner/src/actions/openspec.ts:355` and `packages/runner/src/actions/openspec.ts:360`. `ArchiveChangePersistsArchiveNameBeforeMove` verifies write-before-move and the simple payload in `packages/runner/tests/openspec.spec.ts:1012`.
- AC3 is satisfied: the missing-source branch calls `findExistingArchive()`, validates `basename(existingArchive)`, persists it via `persistArchiveName()`, and only then continues at `packages/runner/src/actions/openspec.ts:321`. `ArchiveChangeBackfillsArchiveNameWhenSourceMissingAndArchiveExists` covers the success path at `packages/runner/tests/openspec.spec.ts:1261`, and `ArchiveChangeBackfillPersistFailure_ReturnsRetrySafePersistNameWithoutMove` covers persist failure at `packages/runner/tests/openspec.spec.ts:1348`.
- AC4 is satisfied: persisted new-key retries locate `${dirname(changeDir)}/archive/${openspecArchiveName}` without recomputing the current date in `packages/runner/src/actions/openspec.ts:268`. Cross-date coverage exists in `ArchiveChangeCrossDayRetry_ReusesPersistedNameAndFindsArchivedDirectory` at `packages/runner/tests/openspec.spec.ts:1129` and the post-backfill retry test at `packages/runner/tests/openspec.spec.ts:1307`.
- AC5 is satisfied: `readLegacyArchiveName()` reads `_actions.archiveChange.destination[sourceRel]` defensively at `packages/runner/src/actions/openspec.ts:15`, while `resolveEffectiveArchiveName()` gives `openspecArchiveName` precedence at `packages/runner/src/actions/openspec.ts:28`. Legacy-only migration is covered before move at `packages/runner/src/actions/openspec.ts:307` and when the archive already exists at `packages/runner/src/actions/openspec.ts:286`. Tests cover legacy move migration at `packages/runner/tests/openspec.spec.ts:1381`, legacy archive-present migration at `packages/runner/tests/openspec.spec.ts:1426`, and both-key precedence at `packages/runner/tests/openspec.spec.ts:1475`.
- AC6 is satisfied: all `writeVars` errors return `errorCode: "retry-safe"` with `stage: "persist-name"` from `persistArchiveName()` at `packages/runner/src/actions/openspec.ts:470`. The before-move failure test is at `packages/runner/tests/openspec.spec.ts:1241`; the backfill failure test is at `packages/runner/tests/openspec.spec.ts:1348`.
- AC7 is satisfied: the runner tests include before-move persistence, versioned retry, cross-date retry, existing archive backfill, subsequent cross-date retry after backfill, persist failures, legacy fallback/migration, both-key precedence, and unsafe persisted-name validation for both new and legacy keys. Unsafe-name coverage is parameterized at `packages/runner/tests/openspec.spec.ts:1165`.
- Changed files were reviewed using `git diff master...HEAD`: product changes are confined to `packages/runner/src/actions/openspec.ts` and `packages/runner/tests/openspec.spec.ts`; OpenSpec workflow artifacts under `openspec/changes/issue-289/` were treated as review context per the candidate boundary. No server, database schema, or workflow YAML changes were made.
- Adjacent retry/recovery paths were inspected: existing source/archive conflict handling remains a `partial-archive` failure at `packages/runner/src/actions/openspec.ts:279`; missing source with no matching archive remains `missing-source` at `packages/runner/src/actions/openspec.ts:337`; git staging/commit retry-safe handling remains scoped to `sourceRel` and `destinationRel` at `packages/runner/src/actions/openspec.ts:371`.
- Security and data safety checks passed review: persisted archive names are validated as single path segments by `validateArchivePrefix()` at `packages/runner/src/actions/openspec.ts:543`, and destination resolution remains constrained under the archive root by `resolveArchiveDestination()` at `packages/runner/src/actions/openspec.ts:575`. The legacy key is read-only; grep found product references only in `packages/runner/src/actions/openspec.ts` and tests.

## Verification

- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner` passed: 50 test files passed, 722 tests passed, 23 skipped.
- `git diff --check master...HEAD` passed with no whitespace errors.

<promise>PASS</promise>
