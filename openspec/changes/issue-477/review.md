# Review: Issue 477

## Findings

### [P1] Allow custom Profiles in Issue create/edit validation

`IssueGrain.CreateWithReceiptAsync` and `IssueGrain.ChangeRepositoryWithReceiptAsync`/`UpdateFullAsync` validate a non-null selection with `_profiles.Exists(workflowProfileId)` at `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:799-802` and `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:1123-1125`. `_profiles` is the legacy `IssueWorkflowProfileRegistry`, so a custom Profile created in the new Project collection is rejected even though the API route already validates it through `IWorkflowProfileProvider`. As a result, `mo issue create/edit --workflow-profile <custom-id>` cannot satisfy the explicit-selection acceptance criteria. The Issue binding participant must validate against the current Project collection provider, including slash-capable custom IDs, rather than the legacy global registry.

### [P1] Permit Issue selection changes while an active Run remains bound

Both Issue selection write paths reject any Profile-field update whenever `_issue.WorkflowRunId` is non-null at `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:889-892` and `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:1028-1031`. The issue contract explicitly requires changing an Issue's explicit-versus-inherited selection during an active Run without changing that Run's persisted Profile ID; rejecting the write prevents that required scenario entirely. Selection writes should remain possible while the Run uses its startup binding, with the existing Run lock protecting only the Run's binding.

### [P1] Route every Issue Profile selection write through the Issue coordinator

`IssueRoutes.Crud` sends non-repository PATCHes directly to `grain.UpdateFullAsync` at `packages/server/src/Mohist.Server/Api/IssueRoutes.Crud.cs:328-342`, while only repository-bearing PATCHes use `IIssueRepositoryCoordinatorGrain`. This means an `--workflow-profile` edit without a repository field bypasses `IssueRepositoryCoordinatorGrain`, contrary to the design and acceptance requirement that Issue Profile selection be serialized with Issue lifecycle operations. The direct path can race coordinator create/repository operations and does not provide the required single coordinator fence/receipt semantics; Profile selection edits need to use the coordinator path as well.

### [P1] Do not persist a new Run before Profile binding succeeds

`WorkflowGrain.PersistProfileBindingAsync` calls `_runStore.SaveAsync(_run!)` before invoking `BindWorkflowRunAsync` at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:618-628`. If the Profile is deleted after `LoadStartupStructureAsync` reads it but before the coordinator revalidates membership, the coordinator returns `ProfileUnknown` after the Run row has already been saved with its public Profile ID and no custom backing key. The method then throws, leaving a persisted Run that was never successfully bound. Startup must make creation and binding atomic from the caller's perspective, or compensate/remove the newly created Run when coordinator binding fails, so no unbound Run is committed.

<promise>FAIL</promise>
