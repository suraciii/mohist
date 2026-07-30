## Why

`docs/workflow-profiles.md:7` draws the boundary: Variables and Prompts are
independent resources that do not belong to Workflow Profile. The code does not
follow it. Three classes each bundle multiple resources behind the word
"Profile", so none can be named honestly: `ProjectWorkflowProfileManager`
(Profile CRUD + Project Variables + Prompts + enable toggle),
`IssueWorkflowProfileManager` (Issue template selection + Issue Variables), and
`WorkflowProfileManager` (Definition resolution + variable merge + prompt
render). "Profile" therefore means three different things depending on the file.
A rename cannot fix this; the classes must be cut along the documented resource
boundary.

## What Changes

- **Variables split into per-scope Stores.** Project-scope and Issue-scope
  variable read/write leave the `*WorkflowProfileManager` classes and become
  their own Stores (matching the `Store` role suffix). Run-scope already exists
  as `WorkflowRunVariablesStore`. The Project → Issue → Run merge precedence
  currently inside `WorkflowProfileManager` becomes a single-responsibility
  variable resolver.
- **Prompts split into an independent class.** Prompt CRUD (system catalog +
  project override + preview) and prompt resolution/render leave
  `ProjectWorkflowProfileManager` and `WorkflowProfileManager`; Prompts are no
  longer co-located with Profile CRUD.
- **Definition resolution separated from the above.** The effective-Profile
  cascade (bound Profile → effective Profile → stage specs → structure → approval
  config) remains, stripped of variable and prompt concerns, as a single
  definition resolver.
- **Legacy template CRUD retired; enable toggle migrates to the Profile
  authority.** `WorkflowProfileDataMigrator` already migrates legacy
  project-templates / Issue-templates / Issue-default-cascade into the
  Project-scoped Profile collection. Once migration is the sole path, the legacy
  `ProjectWorkflowTemplate` / `IssueWorkflowProfile.Template` CRUD is deleted.
  The system-Profile enable toggle (`GetDisabledWorkflowProfileIdsAsync` /
  `SetProfileEnabledAsync`) moves from `ProjectWorkflowProfileManager` onto
  `IWorkflowProfileProvider` — enablement is a Profile-membership concern, and the
  provider is already the membership authority. `WorkflowProfileProvider` becomes
  the only Profile authority for membership, definition, and enablement.
- **`IIssueWorkflowProfile` split.** Its descriptive face (`Id` / `DisplayName`
  / `Description` / `IsDefault` / `Definition`) reuses `WorkflowProfile`; the two
  `ProjectWorkflowState(...)` overloads become their own state-projection
  abstraction. The "Profile is what" and "how an Issue projects workflow state"
  concerns no longer share an interface.
- **Naming follows `conventions.md`.** Split classes take the suffix their role
  warrants (`Store` for a persistence boundary, `Resolver`/`Manager` per the
  table). A repo-wide grep for "Profile" then refers to exactly one resource.
- All external behavior is preserved: API / CLI / Web surface, response shapes,
  Definition parsing, variable merge precedence, and prompt rendering results are
  unchanged. No data migration is added beyond the existing one.

## Capabilities

- `workflow-variables`: Variables read/write per scope (Project, Issue, Run) and
  the Project → Issue → Run merge precedence — the per-scope Stores and the
  merge resolver, including existing sanitize and `agent.runtime` rejection rules.
- `workflow-prompts`: Prompt read/write (system catalog + project override) and
  prompt resolution/rendering as an independent resource, no longer co-located
  with Profile CRUD.
- `workflow-profile-resolution`: The Definition resolution cascade (effective /
  bound Profile → stage specs → structure → approval config) as a single
  resolver, plus retirement of legacy template CRUD so `WorkflowProfileProvider`
  is the sole Profile authority.
- `issue-workflow-projection`: Splitting `IIssueWorkflowProfile` — the Profile
  descriptive face reuses `WorkflowProfile`; Issue workflow-state projection
  (`ProjectWorkflowState`) becomes its own abstraction.

## Impact

- **Server (`packages/server`):**
  - `Workflow/Services/ProjectWorkflowProfileManager.cs` — decomposed into a
    Project Variables Store and a Prompts store/manager; the enable toggle and
    legacy template CRUD move onto `IWorkflowProfileProvider`, after which the
    class is deleted.
  - `Workflow/Services/IssueWorkflowProfileManager.cs` — decomposed into an
    Issue Variables Store; the (already-uncalled) inline-template write path is
    removed, leaving Issue Profile selection as a pure Profile-id reference.
  - `Workflow/Services/WorkflowProfileManager.cs` — reduced to a definition
    resolver; variable-merge and prompt logic extracted to their own resolvers.
  - `Workflow/Services/WorkflowProfileProvider.cs` (`IWorkflowProfileProvider`)
    — becomes the sole Profile authority for membership, definition, **and
    enablement** (gains `GetDisabledProfileIdsAsync` / `SetProfileEnabledAsync`).
  - `Issue/Services/WorkflowProfiles/IIssueWorkflowProfile.cs`,
    `MohistIssueWorkflowProfileBase.cs` — descriptive face reuses `WorkflowProfile`;
    `ProjectWorkflowState` projection separated.
  - Consumers rewired by injected type: `Api/ProjectRoutes.cs`,
    `Api/IssueRoutes.Crud.cs`, `Api/IssueRoutes.WorkflowProfile.cs`,
    `Issue/Grains/IssueGrain.cs`, `Workflow/Grains/WorkflowGrain.cs` (incl.
    `IWorkflowGrainContext`), `Workflow/Services/WorkflowQuerier.cs`,
    `Issue/Services/IssueQuerier.cs`, `Issue/Services/IssueMetricsQuerier.cs`,
    `Issue/Services/IssueReadModelLoader.cs` (the last three switch their
    enable-toggle read to the provider).
  - DI auto-registration relies on the `IScopedService` marker; new split types
    carry it, so `MohistServiceRegistration` needs no manual edits for them.
- **Persistence:** the legacy `ProjectWorkflowTemplate` table and
  `IssueWorkflowProfile.Template` column are read-only-then-removed once the
  existing migration is the sole path (no new migration introduced here).
- **API / CLI / Web:** no change — surfaces and response shapes preserved.
- **Tests:** spec + unit tests updated as types move; architecture tests
  (`ArchitectureRules`, `ProductionContractRules`, `WorkflowGrainContractRules`)
  referencing the old names updated. Server full suite (unit + spec + arch)
  stays green between each landing step; Web typecheck + tests green.
