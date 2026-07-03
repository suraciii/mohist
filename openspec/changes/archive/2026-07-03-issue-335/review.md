# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: typos
  Evidence: `IssueQuerier.cs:1494` had a stale comment `"Captures WorkStarted / WorkCompleted / Closed / Reopened so"` referencing the legacy event names. The comment was updated to `"Captures WorkStarted / Completed / Cancelled / Reopened so"` to align with the renamed vocabulary.
  Verification: `rg 'WorkCompleted.*Closed' packages/server/src/` returns zero matches.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `IssueEventSerializer.cs`
  Evidence: The `IssueWorkStarted` arm at line 32 uses a bare literal `"com.mohist.issue.work-started"` instead of `EventCatalog.ReverseDns.IssueWorkStarted`, even though the catalog constant exists at `EventCatalog.cs:128`. The two terminal events correctly route through catalog constants; the remaining non-terminal arms still use bare literals. The design explicitly scoped the rename to terminal events only and omitted a full serializer-to-catalog alignment, but the existing `IssueWorkStarted` catalog constant makes the gap visible.
  SuggestedAction: Route `IssueWorkStarted` through `EventCatalog.ReverseDns.IssueWorkStarted` for consistency, or defer to the broader "catalog lists 4 of 13 emitted ids" fix.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `StagePopulationSnapshotService.cs`
  Evidence: Lines 298-301 and 316 mix bare string literals (`"com.mohist.issue.work-started"`, `"com.mohist.issue.reopened"`) with catalog constants (`EventCatalog.ReverseDns.IssueCompleted`, `EventCatalog.ReverseDns.IssueCancelled`) in the same conditional blocks. The two terminal events correctly route through constants; the non-terminal literals are unchanged per the design scope but create a readability inconsistency when placed alongside the renamed ones.
  SuggestedAction: Map `work-started` and `reopened` to their respective catalog constants (or to shared local constants) when the broader catalog alignment is done.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `BackfillIssueEventsTerminalTypeRenameMigrationSpecs.cs`
  Evidence: The test helpers `RunMigrationUpAsync`/`RunMigrationDownAsync` execute raw SQL directly rather than invoking the EF `Migration` class methods. This tests the SQL correctness but does not verify the migration class wiring (attribute, `Up`/`Down` method signatures). The separate test `DatabaseMigrate_IncludesBackfillIssueEventsTerminalTypeRenameMigration` confirms the migration is registered and `MigrationClass_DoesNotOverrideBuildTargetModel` validates the structural contract.
  SuggestedAction: Consider adding a test that runs `new BackfillIssueEventsTerminalTypeRename().Up(migrationBuilder)` to verify the class's `Up`/`Down` method wiring. Low priority since the raw SQL smoke tests are already exhaustive.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: `20260629120000_BackfillIssueCompletedAt.cs`
  Evidence: The historical migration at `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260629120000_BackfillIssueCompletedAt.cs` references the legacy ids `com.mohist.issue.work-completed` and `com.mohist.issue.closed` in its SQL (lines 43, 57). Design D6 explicitly treats this as immutable history — EF never re-runs already-applied migrations, so these references are harmless on upgraded databases and no-ops on fresh databases.
  SuggestedAction: None required. Do not edit historical migrations.
  Status: pre-existing

- [ID: item-6]
  Severity: info
  Scope: `20260629120000_BackfillIssueCompletedAtMigrationSpecs.cs`
  Evidence: The test file for the historical `completedAt` backfill migration seeds rows with the legacy ids `com.mohist.issue.closed` and `com.mohist.issue.work-completed`. These test the historical migration's SQL, which intentionally operates on the legacy vocabulary.
  SuggestedAction: None required. Historical migration tests correctly verify against their own vocabulary.
  Status: pre-existing

- [ID: item-7]
  Severity: info
  Scope: `IssueEventSerializer.cs` non-terminal arms
  Evidence: Non-terminal event serializer arms (`IssueCreated`, `IssueWorkStarted`, `IssueArchived`, `IssueUnarchived`, `IssueReopened`, etc.) use bare string literals. These were explicitly excluded from the rename scope (proposal: "Non-Terminal event names (created, work-started, labels-changed, etc.) are untouched"). The catalog's broader "4 of 13 emitted ids" deficit is a deferred concern.
  SuggestedAction: Address in a follow-up change that aligns the full serializer-to-catalog vocabulary.
  Status: out-of-scope

<promise>PASS</promise>
