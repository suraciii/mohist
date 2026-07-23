# Review: Issue 477

## Findings

### [P1] Make initial Run binding recoverable across a crash

`WorkflowGrain.PersistProfileBindingAsync` first persists the new Run and only then calls `WorkflowProfileReferenceCoordinator` at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:618-637`. A process crash or grain deactivation between those operations leaves an active Run with its public `workflowProfileId` but without the custom `WorkflowProfileIdKey` backing reference. The deletion query can currently notice the public ID in the serialized state, but the required nullable FK invariant is still broken and there is no durable repair operation for the missing binding; a later state rewrite or query path can therefore allow deletion without a database reference protecting the active Run. Persist the binding as a recoverable operation, or reconcile every active Run with a missing custom backing key before Profile deletion and before future Run mutations.

### [P1] Do not replay a delayed binding onto a terminal Run

`WorkflowRunBindingParticipant.BindAsync` unconditionally writes `WorkflowProfileIdKey` for a non-built-in Profile at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowRunBindingParticipant.cs:30-43`; it does not inspect the persisted Run status. The startup binding is a separate durable command from Run persistence, so a crash can leave a pending bind in `WorkflowProfileReferenceCoordinator`. If the Run is subsequently stopped or completed and its normal save clears the backing key, coordinator activation can replay the old bind and write the key back onto the terminal Run. The Profile then remains undeletable because of the restrictive FK even though terminal Runs are explicitly allowed to release that key, and deletion reports an opaque not-found/FK result rather than succeeding. Make binding reject or no-op for terminal Runs, and add a replay-after-terminalization test that verifies the public Profile ID and history remain while the backing key stays null.

<promise>FAIL</promise>
