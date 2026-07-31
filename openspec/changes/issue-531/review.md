# Review: Issue 531

## Findings

### H1. Legacy running jobs can be redelivered with a different execution envelope

The running-row fallback in `AgentJobOwnerLedger` reconstructs only `with.prompt` (`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260729000000_AgentJobOwnerLedger.cs:264-281`). A normal owner-led AgentJob dispatch also includes workspace in `Variables` and instructions, model, variant, runtime, and skills in `With` (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:1047-1088`). A valid legacy Running row that has no persisted `dispatchSnapshot` but does have any of those inputs migrates successfully; its next poll then redelivers the incomplete fallback envelope and runs with different/default execution inputs. This violates the required reconstructable dispatch ledger. Reconstruct the complete current `WorkDispatch` payload from legacy input, or reject rows that cannot do so, and add migration coverage for a full running dispatch context.

### H2. PascalCase legacy AgentJobs are accepted by migration but invisible to owner-ledger queries

The migration deliberately reads both camelCase and PascalCase state members (`AgentJobOwnerLedger.cs:205-224`, `:259-291`), but it does not normalize the JSON. The generated `AgentJobs.Status` and `ProjectId` columns only extract camelCase paths (`packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs:254-263`), while all polling and closeout projections filter on `Status` (`packages/server/src/Mohist.Server/Infrastructure/Data/AgentJobs/AgentJobStore.cs:278-341`). For example, a valid legacy `{"Status":"running","RunnerId":"r","WorkId":"w","RunningSince":"...","Input":{"Prompt":"p","AgentId":"a"}}` row receives its runner/work projection but retains a null generated `Status`; `ListRunningForRunnerAsync("r")` will not return it. The Runner cannot redeliver, display, authorize logs for, or close out that work. Normalize migrated state to the canonical casing (or make the generated projections handle both forms) and cover PascalCase pending and running rows.

## Verification

- Compared `origin/master...HEAD` with the Issue 531 plan artifacts and acceptance criteria.
- Rechecked the current migration, owner-ledger projections, and dispatch construction.
- The current full Server spec suite was reported passing by the focused review pass; the missing legacy-state cases are not covered.

<promise>FAIL</promise>
