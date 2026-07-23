# Review: Issue 477

## Findings

### [P1] Restore a green server test suite after switching startup to the collection

`npm test` currently fails `Mohist.Server.SpecTests` with 17 failures (2925 passed, 17 failed). The failures are caused by this change's strict path in `WorkflowProfileManager.LoadStartupStructureAsync` (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileManager.cs:193-207`): existing workflow fixtures create a Project/Issue context without a `ProjectWorkflowProfiles` row and now fail with `has no default Workflow Profile`. Representative failures are `WorkflowGrainStateSaveFailureSpecs.EnsureStarted_DuplicateDeliveryRefreshesCurrentContextWithoutRestarting`, `WorkflowGrainStateSaveFailureSpecs.RefreshIssueContext_TerminalRunNoops`, `AgentActivityObservabilitySpecs.AgentActivity_WhenRunnerActiveWorksExceedVisibleSessions_SlotsReflectRunner`, and `RuntimeEntrySpecs.AgentStatus_WhenRunnerActiveWorksExceedVisibleSessions_CapacityReflectsRunner`.

The same run still contains legacy contract specs that conflict with the delivered collection model: `IssueTemplateApiSpecs.DisabledDefault_DoesNotAffectOtherProjects` and `DisabledBuiltIn_CanBeShadowedByProjectCustomTemplate` fail on the new unique `ProjectWorkflowProfiles.ProjectId` row, while `IssueCreationSpecs.StartWorkflow_UsesProjectDefaultTemplate` and `IssueWorkflowLifecycleSpecs.UpdateFullAsync_WhenWorkflowHasStarted_ChangesIssueSelectionWithoutChangingRunBinding` assert the removed template cascade. Update or remove those obsolete specs and migrate all still-valid workflow fixtures to seed a Project collection/default, while retaining explicit missing-collection/default failure specs. The repository must not merge with its default test command red.

<promise>FAIL</promise>
