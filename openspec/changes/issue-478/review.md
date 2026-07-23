# Review Findings

## P0: The clean Variables API was not delivered

The new CLI constructs all Project, Issue, and Run resource requests as `/variables` in `packages/cli/Mohist.Cli/VariableCommands.cs:513-520`, but the server still maps only `workflow-profile/variables`: `packages/server/src/Mohist.Server/Api/ProjectRoutes.cs:421-439`, `IssueRoutes.WorkflowProfile.cs:59-112`, and `WorkflowRoutes.cs:101-121`. Consequently every newly added CLI read/write request receives a missing route instead of reaching the Variables resource, while the legacy routes remain mapped contrary to the acceptance criteria. The same migration is incomplete for non-CLI callers: `packages/runner/src/server/connection.ts:282` and the production Web clients at `packages/web/src/entities/settings/api/client.ts:64-68` and `packages/web/src/entities/issue/api/client.ts:266-281` still call the old path. Rename the three server route sets, remove the old mappings, and update these callers plus their handlers/specs so the CLI and existing product paths use the same resource.

## P0: Invalid Variables roots are still persisted

The required write-boundary validation is absent: there is no `VariableBundleShapeValidator`, and the manager write entry points still save incoming data directly. In particular, `ProjectWorkflowProfileManager.SetVariablesAsync` sanitizes and persists at `packages/server/src/Mohist.Server/Workflow/Services/ProjectWorkflowProfileManager.cs:239-263`, `IssueWorkflowProfileManager.SetVariablesAsync` persists at `packages/server/src/Mohist.Server/Workflow/Services/IssueWorkflowProfileManager.cs:142-164`, and `WorkflowRunProfileManager.SetVariablesAsync` delegates directly to mutation at `packages/server/src/Mohist.Server/Workflow/Services/WorkflowRunProfileManager.cs:45-47`. A JSON scalar/array in `vars` or `stages.<stage>.vars` can therefore pass the manager boundary, violating the rejection and unchanged-original requirements for both HTTP and grain callers. Add the shared validator at the start of every Set/Patch path, before merge, filtering, or persistence, and cover both root locations and unchanged-state behavior.

## P1: Effective stage list is rejected instead of implemented

`run variable list --effective --stage <stage>` is explicitly rejected by `packages/cli/Mohist.Cli/VariableCommands.cs:100-104` with the message that the options are mutually exclusive. The issue acceptance criteria and `specs/variable-commands/spec.md:81-91` require effective reads to support `--stage`, including list behavior, and `variable-resources/spec.md:56-66` requires the effective stage projection. Remove this rejection and send the effective list request with `?stage=<stage>`; add a regression test that asserts the request and returned stage projection.

<promise>FAIL</promise>
