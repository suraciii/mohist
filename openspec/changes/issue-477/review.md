# Review: Issue 477

## Findings

### [P1] Do not leave an active Run without its binding backstop after a crash

`WorkflowGrain.PersistProfileBindingAsync` saves the newly-created Run at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:618-631`, then performs the separate coordinator binding call. If the process crashes or the grain is deactivated after the first save and before the coordinator participant commits, the Run remains active with its public `workflowProfileId` but a null `WorkflowProfileIdKey`. On restart there is no repair/retry path for this missing binding, so `WorkflowProfileDeletionBlockerQuery` omits the active Run and the custom Profile can be deleted while the Run still resolves it for future Stages. Persist the binding as a recoverable operation or otherwise reconcile an unbound active Run before allowing Profile deletion; the nullable FK must never be left null for an active custom-Profile Run.

### [P1] Replay an unfinished delete fence instead of discarding it

`WorkflowProfileReferenceCoordinatorGrain.ReplayPendingAsync` explicitly clears a pending `DeleteProfile` fence without invoking the deletion at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowProfileReferenceCoordinatorGrain.cs:288-329`. If activation occurs after the fence is persisted but before `WorkflowProfileProvider.DeleteAsync` commits, the durable command is acknowledged as recovered even though the Profile still exists. A retry may eventually delete it, but the original command can return without applying its requested operation and the process manager loses the only durable record needed to complete it. Replay the delete operation idempotently, or retain/retry the fence until the delete has a definitive result.

### [P1] Fail migration when a legacy Definition cannot be converted

`WorkflowProfileDataMigrator` catches every conversion exception and continues at `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowProfileDataMigrator.cs:163-168` and `:194-200`. The migration therefore can finish successfully while leaving a legacy custom template or inline Issue Definition out of the new collection; `DatabaseInitializer` ignores the returned diagnostics at `packages/server/src/Mohist.Server/Infrastructure/Data/Db/DatabaseInitializer.cs:17-20`. Because the legacy template/profile write surfaces are removed, that Definition is then inaccessible, and a referenced invalid template can instead fail later as a foreign-key error. Migration must fail atomically with the Project/Profile or Issue identity and conversion diagnostics, rather than silently dropping an object required by the acceptance criteria.

<promise>FAIL</promise>
