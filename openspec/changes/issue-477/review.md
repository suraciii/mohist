# Review: Issue 477

## Findings

### [P1] Map the Issue FK race to the required conflict

`IssueStore` translates a deletion-vs-selection FK violation into `WorkflowProfileNotFoundException` (`packages/server/src/Mohist.Server/Infrastructure/Data/Issue/IssueStore.cs:108-113`), but the create path rethrows that exception from `IssueRepositoryCoordinatorGrain.CreateIssueAsync` (`packages/server/src/Mohist.Server/Issue/Grains/Coordinator/IssueRepositoryCoordinatorGrain.cs:95-127`) and `IssueRoutes.Crud` only maps `UnknownWorkflowProfileException` for `POST /issues` (`packages/server/src/Mohist.Server/Api/IssueRoutes.Crud.cs:113-125`). The profile-only PATCH path also accepts the coordinator result generically at `IssueRoutes.Crud.cs:336-379`, without handling `IssueRepositoryBindingResultCode.WorkflowProfileNotFound` as the repository-bearing PATCH does at lines 304-325. When deletion commits first, Issue create/edit therefore returns an unclassified server failure or a generic conflict instead of the retryable `workflow-profile-not-found` response required by the collection and selection specs.

### [P1] Do not recompute migrated Project defaults from the legacy column

`WorkflowProfileDataMigrator` uses `ProjectWorkflowProfiles.DefaultTemplateId` as the source on every invocation (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowProfileDataMigrator.cs:261-279`), while it deliberately leaves that legacy column unchanged. After a reserved custom `mohist/*` default is migrated to `legacy-reserved/...`, the next startup sees the old `DefaultTemplateId` again, has an empty in-memory rename map, and overwrites `DefaultWorkflowProfileId` back to the built-in `mohist/...` ID. This loses the migrated Project reference and can make the Issue/Run references inconsistent with the Project default. The startup migration must be idempotent and preserve an already-written `DefaultWorkflowProfileId` (or persist/use an unambiguous migration marker) rather than deriving it from the stale legacy field.

### [P1] Recognize the actual terminal WorkflowRun statuses during migration

The domain considers only `Stopped` and `Completed` terminal (`packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.cs:16-26`), but `WorkflowProfileDataMigrator` tests for the unrelated strings `done`, `failed`, and `cancelled` (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowProfileDataMigrator.cs:317-328`). Existing completed or stopped custom Runs are consequently migrated with a non-null `WorkflowProfileIdKey`, so their restrictive FK continues to block Profile deletion even though the acceptance criteria require terminal Runs to release that backing key while retaining their public ID and history. The migration must use the domain terminal states, including the serialized enum representation actually persisted.

<promise>FAIL</promise>
