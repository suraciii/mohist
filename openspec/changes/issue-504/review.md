## Findings

### P2: Validate Issue context before first Run creation

`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:248` constructs `WorkflowRunMetadata` directly from `WorkflowIssueContext` without validating `ProjectId` or `IssueNumber`. Before this change, this path called `WorkflowRunLineage.AnnotationsFor`, which rejected a blank Project ID and non-positive Issue number. `WorkflowIssueContext` itself is a public serializable record (`packages/server/src/Mohist.Server/Workflow/Grains/IWorkflowGrain.cs:54`) and does not enforce either invariant, so a caller can now persist an Issue-backed Run with an empty project or `0`/negative issue number. That violates the typed-context requirement and can leave ownership, projection, and event lineage without the required Issue context. Validate this context through the same boundary used by `ForIssue` (including normalizing a non-positive Epic to absent), before creating the metadata, and add cases for invalid initial contexts.

## Verification

`npm test -- --filter TypedWorkflowRunLineageMigrationSpecs` completed the full .NET test suite successfully (3281 server spec tests). Its forwarded Vitest filter intentionally matched no Web or Runner tests, so those workspace test commands exited non-zero after the server suite.

<promise>FAIL</promise>
