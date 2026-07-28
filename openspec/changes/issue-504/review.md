## Findings

### P2: Validate typed metadata supplied through StartAsync

`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:233` accepts `WorkflowStartInput.Metadata` verbatim and `RequireProjectOwnership` at line 701 only checks that `ProjectId` is non-empty. Consequently, the public `StartAsync` path can persist `ProjectId = "proj_1", IssueNumber = 0` (or a negative value), or a generic Run with `IssueNumber = null, EpicNumber = 7`. This bypasses the `ForIssue` validation added for `EnsureStartedAsync`, stores an invalid/non-generic typed context, and permits an Epic lineage extension without an Issue. Normalize or reject typed metadata at this creation boundary: an Issue-backed input must have a positive Issue and use the same normalization as `ForIssue`; a generic input must clear or reject both Issue and Epic. Add coverage for these `StartAsync` metadata cases.

<promise>FAIL</promise>
