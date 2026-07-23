# Review: Issue 477

## Findings

### [P1] Match the persisted Run profile field when checking deletion blockers

`WorkflowProfileDeletionBlockerQuery.ListActiveRunsAsync` falls back to `ReadProfileId(row.State)` at `packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileDeletionBlockerQuery.cs:80-83`, but `ReadProfileId` only reads `metadata.annotations.workflowProfileId` at lines 87-97. A real `WorkflowRun` is serialized with its public `WorkflowProfileId` at the JSON root (the same shape read by `WorkflowProfileManager.LoadBoundProfileIdAsync`), not under the annotation map. Therefore an active custom-profile Run whose nullable FK backing key is missing is omitted from the blocker result, and the coordinator can delete the Profile despite the active Run reference. Read the actual persisted Run field (while retaining any explicitly supported legacy shape) and add a regression using `WorkflowRunStore` serialization rather than the annotation-shaped fixture.

### [P1] Retain coordinator fences after transient participant failures

`SetProjectDefaultAsync` and `BindWorkflowRunAsync` clear their persisted fence in the broad `catch` blocks at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowProfileReferenceCoordinatorGrain.cs:115-118` and `160-163`. The same loss occurs during activation: `ReplayPendingAsync` catches every exception for these command kinds, logs it, and then clears `Pending` at lines 328-338. A transient database/grain failure after the fence is written can therefore leave the Project default or Run binding unapplied with no durable command left to retry. Keep the fence for retryable failures and clear it only after an applied or definitively rejected result; add a failure/re-activation test for both participant paths.

<promise>FAIL</promise>
