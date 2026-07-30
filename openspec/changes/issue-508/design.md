## Context

`docs/workflow-profiles.md:7` states that Variables and Prompts are independent
resources, not part of Workflow Profile. The server code does not honor this:
three classes bundle several resources behind the word "Profile".

Current shape (all under `packages/server/src/Mohist.Server/`):

- `Workflow/Services/ProjectWorkflowProfileManager.cs` (567 lines) — Profile
  template CRUD over the legacy `ProjectWorkflowTemplates` table, the system
  template catalog, Project default template, **Project Variables**, the Profile
  enable toggle, and the entire **Prompts** surface (system catalog, project
  override, preview).
- `Workflow/Services/IssueWorkflowProfileManager.cs` (240 lines) — Issue template
  selection plus **Issue Variables**.
- `Workflow/Services/WorkflowProfileManager.cs` (572 lines) — the Definition
  resolution cascade (bound/effective Profile → stage specs → structure →
  approval config), the **variable merge** (Project → Issue → Run), and the
  **prompt resolution/render** path.
- `Issue/Services/WorkflowProfiles/IIssueWorkflowProfile.cs` — a Profile
  descriptive face (`Id`/`DisplayName`/`Description`/`IsDefault`/`Definition`)
  fused with two `ProjectWorkflowState(...)` projection overloads. The projection
  is already implemented profile-independently in the static
  `MohistDefaultWorkflowProjection`, but is reached through the Profile interface.

The new Profile collection authority already exists:
`Workflow/Services/WorkflowProfileProvider.cs` (`IWorkflowProfileProvider`),
backed by `WorkflowProfileRecordRow`, and `WorkflowProfileDataMigrator` already
converts the legacy project-templates / Issue-templates / default-cascade into
that collection. Run-scope variables already have a dedicated
`WorkflowRunVariablesStore`.

Constraints:

- External behavior is preserved (API/CLI/Web surfaces, response shapes,
  resolution results). This is a cut-along-resource-boundaries refactor.
- DI auto-registers any type implementing `IScopedService`
  (`Infrastructure/Hosting/ServiceCollectionExtensions.cs`), so split types carry
  that marker and need no manual registration.
- `WorkflowGrainContractRules.WorkflowProfileManager_ResolutionFailureSites_ThrowTypedException`
  reads an embedded `WorkflowProfileManager.cs` and asserts ≥3
  `WorkflowDefinitionResolutionException` throws. The three throw sites are all
  Definition-resolution failures; they stay together in the definition resolver,
  so the assertion survives a rename but the test's filename reference updates.

## Goals / Non-Goals

**Goals:**

- Each class owns exactly one resource. A repo-wide grep for "Profile" refers to
  Workflow Profile alone.
- Variables: per-scope Stores (Project, Issue) plus a single merge unit; Run
  scope already exists.
- Prompts: one independent component (CRUD + resolution + render), no Profile
  CRUD co-location.
- Definition resolution: one resolver, stripped of variable/prompt concerns.
- Legacy template CRUD (`ProjectWorkflowTemplates`, `IssueWorkflowProfile.Template`
  column) retired once migration is the sole path; `WorkflowProfileProvider` is
  the only Profile authority.
- `IIssueWorkflowProfile` split: descriptive face reuses `WorkflowProfile`;
  workflow-state projection becomes its own abstraction.
- Stepwise landing: each step independently reviewable/revertible, server full
  suite green between steps.

**Non-Goals:**

- No generic base class for the three variable Stores (their key shapes differ;
  `design/README.md` forbids inventing a shared domain concept for coincident
  shapes).
- No change to variable layer count or merge semantics, API/CLI/Web command
  surface, or response shapes.
- No new data migration beyond the existing `WorkflowProfileDataMigrator`.
- No rewrites of healthy code or new abstractions beyond the resource cut.

## Decisions

### D1 — Cut by resource, not by CRUD-vs-read layer

Each split type owns one resource end-to-end (its own read and write), rather
than a horizontal "all reads here / all writes there" layer. A resource cut keeps
each change axis local (e.g. editing prompt precedence never touches variable
persistence), matches the documented three-resource boundary, and is what makes
honest class names possible. The alternative (read-services vs write-stores)
would re-couple resources at the read layer and preserve the naming problem.

### D2 — Target decomposition

| New type (proposed) | Role suffix | Owns | Reads/writes |
|---|---|---|---|
| `ProjectVariableStore` | Store | Project Variables | `ProjectWorkflowProfile.Variables` |
| `IssueVariableStore` | Store | Issue Variables | `IssueWorkflowProfile.Variables` |
| `WorkflowRunVariablesStore` (exists) | Store | Run Variables | `WorkflowRunProfileRow.Variables` |
| `WorkflowVariableResolver` | Resolver | Project→Issue→Run merge + stage resolve + workspace identity | the three Stores |
| `ProjectPromptStore` | Store | system catalog + project prompt CRUD + preview | `ProjectWorkflowProfile.Prompts` + `IPromptLoader` |
| `WorkflowPromptResolver` | Resolver | run-scoped effective prompt resolution | `ProjectPromptStore` + `IPromptLoader` |
| `WorkflowDefinitionResolver` | Resolver | bound/effective Profile cascade, stage specs, structure, approval | `IWorkflowProfileProvider` |
| `WorkflowProfileProvider` (exists) | — | sole Profile collection **and enablement** authority | `WorkflowProfileRecordRow` + catalog + `ProjectWorkflowProfile.DisabledWorkflowProfileIds` |
| `MohistDefaultWorkflowProjection` (exists) | — | Issue↔WorkflowRun state projection | none (pure) |

Each new type implements `IScopedService` so DI picks it up without manual
registration. Consumers swap the injected concrete type; route handler
signatures change the parameter type only.

### D3 — `Resolver` suffix for the three run-context resolution units

`conventions.md` reserves `Resolver` for "external name → canonical resource"
(e.g. `ProjectResolver`). The three units that leave `WorkflowProfileManager`
— Definition cascade, variable merge, and prompt resolution — all do the same
thing in shape: they resolve a run's context to one canonical effective resource
(the WorkflowDefinition, the effective VariableBundle, the effective Prompt).
That is the defining behavior of a `Resolver`, so all three take the `Resolver`
suffix: `WorkflowDefinitionResolver`, `WorkflowVariableResolver`,
`WorkflowPromptResolver`.

Alternative considered: fold each merge back into its resource's `Store` as a
read method. Rejected because the variable merge is cross-scope (reads three
Stores) and the issue explicitly requires the merge to leave
`WorkflowProfileManager` as a single-responsibility unit; folding it into a
single Store would re-couple the Stores. `Querier` (single-domain read
projection) was also rejected — these units span multiple scopes/stores, not one
domain. No other conventions suffix fits a cross-store run-context assembly, so
`Resolver` is the decision, not deferred to implementation.

### D4 — Dead prompt-override path is dropped, not moved

`ProjectWorkflowProfileManager` exposes
`GetProjectPromptOverrideAsync` / `SetProjectPromptOverrideAsync` /
`DeleteProjectPromptOverrideAsync` / `ListSystemPromptsAsync`, backed by the
`ProjectPromptTemplates` table. These have **no caller outside tests** — the live
prompt write path is `SetPromptAsync`/`DeletePromptAsync` over
`ProjectWorkflowProfile.Prompts`. Decision: do not carry the override methods or
the `ProjectPromptTemplates` table into the new `ProjectPromptStore`. This is
dead-surface removal, not a behavior change (no route invokes it). Tests
asserting the dead methods are deleted with them.

Alternative considered: move them verbatim to preserve symmetry. Rejected because
they are uncalled and carrying dead code into a freshly-cut component defeats the
honest-naming goal.

### D5 — Legacy template CRUD retirement and enable-toggle migration

Two Profile-authority concerns leave `ProjectWorkflowProfileManager` together,
both landing on `IWorkflowProfileProvider` as the sole Profile authority.

**Enable-toggle migration.** `ProjectWorkflowProfileManager` owns the system
Profile enable toggle: `GetDisabledWorkflowProfileIdsAsync` (read — 6 live
consumers: `IssueGrain`, `IssueQuerier`, `IssueReadModelLoader`,
`IssueMetricsQuerier`) and `SetProfileEnabledAsync` (write — currently uncalled
by any route, but it is the documented Settings management surface, not dead
code to drop). Enablement is a Profile-membership concern, so it moves onto
`IWorkflowProfileProvider` (`GetDisabledProfileIdsAsync` /
`SetProfileEnabledAsync`), which is already the membership authority
(`ContainsAsync`). It reads `ProjectWorkflowProfile.DisabledWorkflowProfileIds`
— the same row the variable and prompt Stores read other columns of; multiple
focused readers of one table are expected and do not couple the concerns. The
six consumers switch from the manager to the provider.

**Legacy template CRUD retirement.** The Definition cascade still falls back to
the legacy `ProjectWorkflowTemplates` table (`LoadTemplateReferenceAsync` →
`LoadProjectTemplateAsync`) for issue-sourced and project-default resolution.
`IssueGrain:318` reads the legacy `GetDefaultTemplateAsync`. Retirement is
sequenced:

1. Switch resolution + IssueGrain default read to `IWorkflowProfileProvider`
   (which already carries everything the migrator produced, including
   `DefaultWorkflowProfileId`), and switch the six enable-toggle consumers to
   the provider.
2. Once no resolution path consults the legacy table, delete the template CRUD
   methods and the `ProjectWorkflowTemplates` read paths.
3. The `IssueWorkflowProfile.Template` inline column: the migrator already
   converts inline Issue definitions to collection Profiles
   (`issue-custom:...`); the inline-YAML write path
   (`IssueWorkflowProfileManager.UpdateTemplateAsync`) is already uncalled by
   any source, so removing it is dead-code removal, not a behavior change.
   Issue selection becomes a pure Profile-id reference.

The enable toggle and the legacy reads both linger in the shrinking
`ProjectWorkflowProfileManager` until this step — an acceptable temporary state,
like the variable merge lingering in `WorkflowProfileManager` until step 2.
After this step `ProjectWorkflowProfileManager` is empty and deleted. The legacy
tables/columns are left read-only until a separate persistence cleanup confirms
no consumer remains; this issue does not add an EF migration to drop columns
(Non-Goal), only removes the code paths.

### D6 — `IIssueWorkflowProfile` split via direct projection call

The projection is already profile-independent
(`MohistDefaultWorkflowProjection.ProjectWorkflowState` ignores which Profile).
Today consumers reach it as `_profiles.Get(id).ProjectWorkflowState(issue, wf)`
(IssueGrain, IssueReadModelLoader). Decision: consumers call the projection
directly (or a thin `IssueWorkflowStateProjector` service if DI is preferred),
and `ProjectWorkflowState` leaves the Profile interface. The descriptive face
(`Id`/`Name`/`Description`/`Definition`/`IsDefault`) is sourced from
`WorkflowProfile` / `IWorkflowProfileProvider` rather than re-declared on the
interface.

Alternative considered: keep a Profile-typed descriptive interface separate from
projection. Rejected — `WorkflowProfile` already is that description; a second
interface would duplicate it exactly as the issue describes.

### D7 — Landing order (each step green + revertible)

1. **Extract Stores** — move Project/Issue variable read/write into
   `ProjectVariableStore` / `IssueVariableStore`; rewire route + grain
   parameters. Behavior-identical; old managers delegate or are consumed
   in-place. Run scope untouched.
2. **Extract variable merge** — `WorkflowVariableResolver`; rewire
   WorkflowGrain/dispatch. Strip merge from `WorkflowProfileManager`.
3. **Extract Prompts** — `ProjectPromptStore` (+ drop D4 dead path) and
   `WorkflowPromptResolver`; rewire ProjectRoutes prompt endpoints and the
   `MohistIssueWorkflowProfileBase` merge.
4. **Rename residual to Definition resolver** — `WorkflowProfileManager` becomes
   `WorkflowDefinitionResolver`; update the arch test's embedded-filename
   reference and the 3-throw assertion target.
5. **Split `IIssueWorkflowProfile`** — move callers to the projection directly;
   source description from `WorkflowProfile`.
6. **Retire legacy template CRUD + migrate enable toggle** — D5 sequence: switch
   the enable-toggle consumers and legacy readers to `IWorkflowProfileProvider`,
   then delete the template CRUD methods and the now-empty
   `ProjectWorkflowProfileManager`.

Earlier steps must not depend on later ones. Each step ends with server unit +
spec + arch tests green.

## Risks / Trade-offs

- [Touching active routes, IssueGrain, and WorkflowGrain in a refactor]
  -> Each step is behavior-preserving and guarded by the full spec suite; the
     step order (D7) keeps each diff small and individually revertible. The
     arch test referencing `WorkflowProfileManager.cs` is updated in the same
     step that renames it.
- [Retiring legacy template CRUD before migration is confirmed sole-path could
   lose custom Profiles] -> D5 gates deletion behind switching all readers to the
     Provider first; deletion happens only after no code path consults the
     legacy table. The migrator is idempotent and already runs on startup.
- [`Resolver` suffix drift from the conventions definition] -> D3 resolves this:
      all three run-context resolution units (Definition, variables, prompts)
      resolve run context to one canonical effective resource — the defining
      behavior of `Resolver`. Decided, not deferred.
- [Enable toggle has 6 live consumers across Issue read paths] -> D5 moves
      enablement onto `IWorkflowProfileProvider` (already the membership
      authority) in the same step as legacy retirement, so the read path stays
      single-sourced and `ProjectWorkflowProfileManager` is deleted rather than
      left holding a stranded concern.
- [Large touched-file surface (~50 files reference the managers)] -> Most are
     test files that reference type names; they update mechanically. The
     behavior-bearing consumers are the bounded set listed in the proposal
     Impact.
- [Dead prompt-override removal (D4) is technically an API-surface narrowing]
  -> The methods are uncalled by any route or source, confirmed by grep; only
     tests reference them. Removing dead surface is within the behavior-preserved
     contract.

## Migration Plan

This is a code refactor with no data migration. Deployment is the normal server
release; `WorkflowProfileDataMigrator` runs on startup as today and must remain
the sole path before step D5 (retirement + enable-toggle migration) lands.
Rollback is `git revert` of the relevant step, since each step leaves the suite
green and introduces no schema or behavior change. Steps may land across
multiple PRs in D7 order; no step requires another to have landed first except
D5 step 2 after D5 step 1.

## Open Questions

- Does `MohistDefaultWorkflowProjection` stay a static class or become a DI
  service? It has no dependencies today, so static is fine; a service is only
  needed if a later change injects dependencies. Decide at the IIssueWorkflowProfile
  split step (D6 / landing step 5).
- Confirm `ProjectPromptTemplates` table (D4) has no external/migration reader
  before dropping the code path; if any startup upgrade reads it, retire the
  table in a follow-up rather than here.
