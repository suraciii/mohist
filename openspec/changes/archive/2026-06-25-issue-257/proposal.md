## Why

An issue's workflow profile selection is not a single product fact today: it is persisted as two divergent stores (the `WorkflowProfileId` field on the issue read model, and the separate `IssueWorkflowProfileManager` template/`SourceTemplateId` state). The create handler drops `WorkflowProfileId` entirely (`IssueRoutes.Crud.cs:66-77` never passes it to the grain), one read path hardcodes the default (`IssueQuerier.cs:402`), and the workflow-profile page recomputes a different `ProfileId` from template state (`IssueRoutes.Helpers.cs:141`). As a result a user who selects `mohist/pr` sees `mohist/default` on the issue detail and `mo issue show`, cannot trust what will run, and has no single CLI entry to change it. We need one consistent fact across create, update, every read model, and startup.

## What Changes

- Establish **one source of truth** for an issue's workflow profile selection; all read models (issue detail, list, `mo issue show`, workflow-profile page, startup resolution) project the same value, falling back to project/system default only when no issue-level choice exists.
- Wire `WorkflowProfileId` from `CreateIssueRequest` through to persistence on create (currently declared in the DTO but dropped by the POST handler).
- Add an official **update entry** for the workflow profile selection on backlog/ready issues across API, CLI, and Web, replacing the implicit divergence between the `WorkflowProfileId` field and the `/workflow-profile/template` endpoint.
- Reject modification of the execution template once an issue has an active/started workflow, returning a clear error; explicitly distinguish run-scoped runtime profile overrides from the issue's template choice.
- Guarantee that the workflow definition used at start matches the profile the user sees on the issue (`mohist/pr` → PR publish/merge path, `mohist/default` → default merge/push path).
- Preserve existing model/stage variable configuration via workflow profile variables; the consistency fix must not alter variable overlay semantics.

## Capabilities

### New Capabilities
- `issue-workflow-profile`: The unified semantics of an issue's workflow profile selection — single source of truth, create/update/read consistency across all entry points, default inheritance, started-issue modification guard, and startup template agreement.

### Modified Capabilities
- `cli-interface`: Add an official CLI entry to view and change an issue's workflow profile; ensure `mo issue create --workflow-profile` and `mo issue show` reflect the unified fact.
- `http-api`: Unify `WorkflowProfileId` on the issue read model with the workflow-profile template endpoint; wire create/persist and add the update route with started-issue guard.
- `web-ui`: Make the issue create selector, issue detail, and workflow-profile page read and write the same workflow profile fact.

## Impact

- **Server**: `IssueRoutes.Crud.cs` (create/persist `WorkflowProfileId`), `IssueRoutes.Helpers.cs` / `IssueRoutes.WorkflowProfile.cs` (unify profile id resolution and template update), `IssueQuerier.cs` (remove hardcoded default at line 402, project unified value), `IssueWorkflowProfileManager` / `WorkflowProfileManager` (startup resolution agreement), and the issue grain persistence path.
- **CLI** (`packages/cli`): `mo issue create`/`show` workflow-profile handling and a new update capability.
- **Web** (`packages/web`): issue create form, issue detail, and workflow-profile page binding.
- **Tests**: regression coverage for create-as-PR, default→PR switch, read-model consistency, started-issue modification rejection, and startup template selection.
- **No breaking changes** to external APIs beyond consolidating two divergent fields into one consistent value; existing variable/prompt overlays are preserved.
