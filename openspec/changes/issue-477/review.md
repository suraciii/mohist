# Review: Issue 477

## Findings

### [P1] Persist the Issue Profile foreign-key backing key

`IssueRow.WorkflowProfileIdKey` is only populated by the migration; the Issue create/edit paths update the Profile ID inside the serialized Issue state but never set or clear this backing column. The only production writes found are in `WorkflowProfileDataMigrator`, while `IssueGrain` updates the selection at `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:886-1062`. Consequently, a custom Profile selected by an Issue is invisible to `WorkflowProfileDeletionBlockerQuery` (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileDeletionBlockerQuery.cs:55-67`) and the restrictive FK does not protect the concurrent delete. The Profile can be deleted while the Issue still points at it, violating deletion protection and the required no-dangling-reference race behavior.

### [P1] Return a Definition for built-in `--yaml` views

Built-in collection entries are created with `DefinitionSource: null` in `packages/server/src/Mohist.Server/Workflow/Services/IWorkflowProfileProvider.cs:105-113`, and `WorkflowProfileProvider.GetDefinitionSourceAsync` also returns `null` for built-ins at `packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileProvider.cs:94-104`. The CLI implements `workflow view <profile> --yaml` by printing only `data["definitionSource"]` in `packages/cli/Mohist.Cli/MohistCliCommands.Workflow.cs:74-77`. Therefore `mo workflow view mohist/local --yaml` and the other readable built-ins produce empty output instead of the built-in Definition, contrary to the collection/view acceptance criteria.

### [P1] Report every active WorkflowRun blocker

`WorkflowProfileDeletionBlockerQuery.FindActiveRunAsync` returns a single row using `FirstOrDefaultAsync` at `packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileDeletionBlockerQuery.cs:69-83`, and `WorkflowProfileDeletionBlockers` exposes only one `ActiveRun` at lines 96-107. If multiple active Runs reference the Profile, deletion reports only the newest one, despite the requirement that the refusal identify every blocking reference relationship. The projection and formatted API error need to retain all active Run IDs/statuses, just as Issue selections are returned as a list.

### [P1] Remove migrated inline Definitions from the runtime source of truth

The migration creates a collection Profile for an inline Issue Definition but leaves the legacy `IssueWorkflowProfile` row, including `Template`, intact in `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowProfileDataMigrator.cs:150-188`. `WorkflowProfileManager.LoadTemplateAsync` still checks that inline `Template` first at `packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileManager.cs:84-100`, before resolving the Project collection. This leaves the old inline Definition as an active precedence path and allows it to override edits to the migrated Profile, violating the collection-only model and the live Definition behavior. The migrated inline data must be cleared or the old resolution path removed as part of the migration.

### [P1] Make startup binding atomic with the coordinator revalidation outcome

`WorkflowGrain.PersistProfileBindingAsync` saves the newly created Run before calling the reference coordinator at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:618-628`. If a custom Profile is deleted after `LoadStartupStructureAsync` confirms membership but before `BindWorkflowRunAsync` revalidates it, the coordinator returns `ProfileUnknown` and the method throws, but the Run row has already been persisted with its public Profile ID and no backing FK. This produces a created Run that was never bound and leaves the state inconsistent with the required startup binding guarantee. The Run creation/binding sequence must handle this race without committing an unbound Run.

<promise>FAIL</promise>
