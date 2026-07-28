# Self Review

## Findings

1. **P1: Workflow task-log project routing is omitted from the implementation plan.** The plan removes `projectId` from every WorkflowRun annotation and requires all lineage consumers to use typed metadata, but [TaskLogService.cs:221](/home/szf/.mohist/projects/workspaces/wr_77d13891d59e4a93981fad5104d979cd/packages/server/src/Mohist.Server/Runner/Services/TaskLogService.cs:221) still resolves a workflow task log's publish scope from `run.Metadata.Annotations["projectId"]`. Neither the design's implementation scope nor `T-001` names this consumer or requires coverage for it. After migration, `ResolvePublishScopeAsync` will return a scope with a null project ID, so task-log notification routing can no longer select the project's subscribers. Add the typed-field switchover and a workflow task-log routing regression test to the design and task acceptance criteria.

<promise>FAIL</promise>
