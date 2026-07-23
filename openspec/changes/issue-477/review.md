# Review: Issue 477

## Findings

### [P1] Preserve migrated legacy-annotation Run bindings during live resolution

`WorkflowProfileDataMigrator` deliberately preserves a legacy Run binding in `metadata.annotations.workflowProfileId` (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowProfileDataMigrator.cs:327-332`). However, `WorkflowProfileManager.LoadBoundProfileIdAsync` only reads the root `workflowProfileId` property (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileManager.cs:406-419`), and `WorkflowRunStore` does not promote the annotation when deserializing (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:111-119,164-169`). Consequently, after an existing migrated Run with the legacy annotation is reloaded, `WorkflowRun.WorkflowProfileId` is null; later stage initialization passes a null bound ID and `LoadTemplateAsync` falls back to the current Issue/Project cascade. Changing either selection after migration can therefore make the active Run resolve a different Profile, violating the requirement that every Run retain its startup Profile ID and that future stages use only that Profile. Read the legacy annotation as part of binding restoration or migrate it into the canonical Run field, and add a recovery/live-resolution spec that changes the Issue or Project selection before a later Stage initializes.

<promise>FAIL</promise>
