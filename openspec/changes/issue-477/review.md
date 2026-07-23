# Review: Issue 477

## Findings

### [P1] Make startup binding failure recoverable

`WorkflowGrain.EnsureCreatedRunAsync` creates the Run and `PersistProfileBindingAsync` saves it through `_runStore.SaveAsync(_run!)` before invoking `WorkflowProfileReferenceCoordinator.BindWorkflowRunAsync` (`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:186-200,618-628`). If the coordinator's membership recheck returns `ProfileUnknown` because a concurrent delete wins the race, startup throws after the Run row and its public `WorkflowProfileId` have already been persisted, with no binding key. The grain still has `_run`, so a retry does not recreate or rebind startup state; the persisted Run can remain unable to resolve its selected Profile. Make startup recoverable or compensate/remove the persisted Run and reset the grain when binding is rejected, and add a delete-before-bind test that retries startup and verifies there is no orphan Run.

### [P1] Treat legacy `done` Runs as terminal during migration

`WorkflowProfileDataMigrator` decides whether a Run is terminal from the serialized state and only recognizes `completed` and `stopped` (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowProfileDataMigrator.cs:333-344`). Existing persistence and the deletion blocker use `done` as another terminal representation, so a migrated terminal Run with `Status = "done"` keeps its custom `WorkflowProfileIdKey` instead of clearing it. That Run then continues to block deletion even though the acceptance criteria allow deletion when only terminal Runs reference the Profile. Use the same terminal-status set as the rest of the persistence model, including `done`, and add migration coverage asserting the key is cleared while the public Profile ID and history remain intact.

<promise>FAIL</promise>
