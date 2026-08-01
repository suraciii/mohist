# Review — issue-536: WorkflowRun State 启动期 backfill 并移除读路径兼容转换

Re-review after the fix commit `681c4613a` ("fix(workflow): drop legacy profile-id fallback
from canonical read paths"). Reviewed the current tree (`master..HEAD` on
`mohist/run-wr_4cb905febea74fb2a47bddcaad5afc29`) against the issue body, the two specs under
`openspec/changes/issue-536/specs/`, `proposal.md`, `design.md`, and `tasks.json`.

## Verification (re-run at HEAD)

- `dotnet build Mohist.sln -p:SkipWebBuild=true` → **0 warnings, 0 errors** (TreatWarningsAsErrors).
- `Mohist.Server.SpecTests` → **3711/3711** (includes `WorkflowRunStateDataUpgraderSpecs` 7/7,
  `WorkflowRunLegacyBindingSpecs` 1/1, `WorkflowRunRerunMigrationSpecs`
  (`LoadAsync_MigratedFailedRun_RerunPersistsFreshStageAttempt`), and the two realigned specs).
- `Mohist.Server.UnitTests` → **1737/1737** (reaches the converter via `InternalsVisibleTo`).
- `Mohist.Server.ArchTests` → **51/51**.

## Prior finding F1 — resolved

F1 reported two service-phase WorkflowRun State read entry points that branched on
legacy-vs-canonical shape (canonical top-level `workflowProfileId` first, then a legacy
`metadata.annotations.workflowProfileId` fallback), violating `canonical-state-read-path`
Requirements 1 and 2. Both are now canonical-only:

- `WorkflowDefinitionResolver.LoadBoundProfileIdAsync`
  (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowDefinitionResolver.cs:232-239`)
  now delegates to the existing canonical-only `ReadWorkflowProfileId`; the legacy annotation
  block is gone.
- `WorkflowProfileDeletionBlockerQuery.ReadProfileId`
  (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileDeletionBlockerQuery.cs:87-107`)
  returns only the top-level `workflowProfileId`; the legacy annotation block is gone.

A `rg` sweep confirms probes for the legacy annotation form now live **only** in the two
cold-start boundaries — `WorkflowRunStateDataUpgrader` and `WorkflowProfileDataMigrator` — and
no service-phase read path parses for historical fields or branches on legacy-vs-canonical
shape. Post-migration behavior is unchanged because the startup upgrader promotes the
annotation to a top-level field.

The two tests that previously relied on the legacy fallback were aligned with the
canonical-read contract rather than weakened:
- `WorkflowProfileCollectionSpecs.SeedWorkflowRunAsync` now emits canonical State (top-level
  `workflowProfileId`), matching the current writer and `SeedIssueAsync`. The
  `Delete_ActiveRunWithMissingBackingKey_StillReportsBlocker` scenario still proves a run with a
  null backing key blocks deletion, now sourcing the profile from the top-level field.
- `WorkflowStageSpecs.LoadStageSpecsAsync_ReloadedLegacyRunKeepsBindingAfterSelectionsChange`
  still seeds legacy annotation State but runs `WorkflowRunStateDataUpgrader.UpgradeAsync` before
  resolution (same pattern as `WorkflowRunLegacyBindingSpecs`), so the resolver observes
  canonical State.

## Acceptance-criteria check

- **Converter confined to the cold-start boundary.** `MigrateLegacyWorkflowRunJson` is
  `internal static` (`WorkflowRunStateDataUpgrader.cs:182`), reachable in production source only
  from `WorkflowRunStateDataUpgrader.UpgradeAsync` (`:37`). `InternalsVisibleTo` is granted only
  to `Mohist.Server.SpecTests` / `Mohist.Server.UnitTests`. Service-phase converter invocations = 0.
- **Enumerated read paths are canonical-only.** All seven call sites across the six files
  (`WorkflowRunStore.Deserialize`, `WorkflowRunQuerier` ×2, `WorkflowQuerier`, `IssueMetricsQuerier`,
  `IssueReadModelLoader`, `ActiveSessionReconciler`) deserialize directly via
  `JSON.Deserialize<WorkflowRun>` / `JsonSerializer.Deserialize<WorkflowRun>` with no converter call
  and no legacy-shape branch. After F1's fix, the two non-enumerated profile-id readers are
  canonical-only too, so the spec's general "every read entry point" wording is now satisfied.
- **Upgrader matches the migration spec.** No-write preflight that names every failing run and
  writes nothing on failure (`AsNoTracking` read); online backup via `source.BackupDatabase` +
  `PRAGMA integrity_check`, with `:memory:` rejection and open-state restoration; single-transaction
  commit over `Chunk(500)` fetches with `CurrentValue = OriginalValue + 1` per rewritten row and
  full rollback on any write failure; byte-ordinal idempotency so canonical rows are untouched and a
  repeat run reports `CandidateCount=0, WrittenCount=0, BackupPath=null`; no lifecycle filter.
  Wired into `DatabaseInitializer.InitializeAsync` after `MigrateAsync` and before the
  dispatch-snapshot and Profile migrations; a throw propagates and blocks the service phase.
- **Spec coverage is real.** `WorkflowRunStateDataUpgraderSpecs` (7) covers full migration,
  preflight failure naming, backup failure, atomic rollback, canonical no-op + idempotency,
  >500-candidate batching, and in-memory rejection. `WorkflowRunRerunMigrationSpecs` covers the
  failed/exhausted-recovery → migrate → load → rerun → reload scenario verbatim.
  `WorkflowRunLegacyBindingSpecs` covers annotation→top-level profile binding after migration.

The runtime-only Done When items (production preflight reporting 254/0, backup integrity,
post-backfill row/ETag counts) cannot be asserted from code review; the code paths that would
satisfy them are present and unit/spec-tested.

## Notes (non-blocking)

- `progress.txt` accurately records the F1 fix and the realigned tests.
- Open Questions (backup retention, converter retirement) remain explicitly deferred Non-Goals.

No problems remain that must be fixed before merge.

<promise>PASS</promise>
