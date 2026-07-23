# Review: Issue 477

## Findings

### [P1] Keep the legacy conversion and collection migration atomic

`DatabaseInitializer.InitializeAsync` calls `WorkflowProfileDataUpgrader.UpgradeAsync` and then `WorkflowProfileDataMigrator.MigrateAsync` as separate operations (`packages/server/src/Mohist.Server/Infrastructure/Data/Db/DatabaseInitializer.cs:17-20`). The upgrader commits its semantic JSON rewrites with `SaveChangesAsync` before the migrator performs reserved-ID collision detection and before it can report conversion diagnostics (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowProfileDataUpgrader.cs:20-41`, `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowProfileDataMigrator.cs:78-142`). If a target collision or later migration diagnostic aborts the migration, the database is left with the legacy template rows already rewritten even though no collection/reference migration completed. This violates the acceptance requirement that a reserved-ID migration failure be atomic and leave no partial writes. Put both phases in one transaction, or make the upgrader purely in-memory/part of the migrator transaction, and add a failure test that verifies legacy rows and all new rows/references are unchanged after a collision.

### [P1] Do not fall back to the removed cascade when a Project collection is missing

`WorkflowProfileManager.LoadStartupStructureAsync` uses the legacy `LoadStructureAsync` path whenever the new provider is unavailable, the Project ID is blank, or no `ProjectWorkflowProfiles` row/default is found (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileManager.cs:187-205`). With a valid Project ID but a missing collection row/default, this returns a legacy structure with `Id = string.Empty`; `WorkflowGrain.EnsureCreatedRunAsync` then skips `PersistProfileBindingAsync` (`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:196-200`). The resulting WorkflowRun has no persisted Profile binding and its later stage resolution can still use the old cascade, contrary to the requirement that every started Run resolve and persist a Profile ID from the current Project collection and that missing `mohist/local` prevent startup rather than silently fallback. Treat a missing Project collection/default as a visible startup failure, and cover startup with a missing collection/default case asserting no unbound Run is created.

<promise>FAIL</promise>
