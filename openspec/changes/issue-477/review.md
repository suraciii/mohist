# Review: Issue 477

## Findings

### [P1] Expose the FK race as `workflow-profile-not-found`

`IssueStore` now translates any custom-profile `DbUpdateException` into `WorkflowProfileNotFoundException` at `packages/server/src/Mohist.Server/Infrastructure/Data/Issue/IssueStore.cs:103-113`, but neither `IssueRepositoryCoordinatorGrain` nor the Issue PATCH route maps that exception to an API result. The coordinator rethrows it from `IssueRepositoryCoordinatorGrain.cs:186-190`, and the profile-only PATCH catches only domain validation and attachment exceptions at `packages/server/src/Mohist.Server/Api/IssueRoutes.Crud.cs:338-375`. When deletion commits first, the required retryable `workflow-profile-not-found` conflict therefore still becomes an unclassified server failure instead of the specified response.

### [P1] Keep the data migration idempotent after a successful rename

`DatabaseInitializer` runs `WorkflowProfileDataMigrator.MigrateAsync` on every startup (`packages/server/src/Mohist.Server/Infrastructure/Data/Db/DatabaseInitializer.cs:17-20`), while the migration intentionally leaves legacy template rows in place. On the second startup, a reserved legacy ID resolves to the already-created `WorkflowProfileRecords` target, and the new preflight at `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowProfileDataMigrator.cs:112-133` treats that normal prior result as an external collision and throws. Thus a successful migration prevents subsequent server initialization whenever a legacy `mohist/*` template exists; the migration needs an idempotent way to distinguish its own previously migrated record from a conflicting existing Profile.

### [P1] Clear the backing key when inserting a terminal Run

`WorkflowRunStore.StageRunAsync` clears `WorkflowProfileIdKey` for terminal Runs only on the existing-row update path at `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:153-157`. A newly inserted terminal Run takes the unconditional custom key at lines 141-147. If a terminal Run is created or restored through this insert path, it retains a restrictive FK reference and blocks deletion even though the acceptance contract says terminal Runs must have a null backing key and remain deletable by Profile ID/history. The insert path must apply the same terminal-status rule.

<promise>FAIL</promise>
