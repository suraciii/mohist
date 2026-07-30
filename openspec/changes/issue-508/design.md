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
| `WorkflowVariableResolver` | Resolver* | Project→Issue→Run merge + stage resolve + workspace identity | the three Stores |
| `ProjectPromptStore` | Store | system catalog + project prompt CRUD + preview | `ProjectWorkflowProfile.Prompts` + `IPromptLoader` |
| `WorkflowPromptResolver` | Resolver* | run-scoped effective prompt resolution | `ProjectPromptStore` + `IPromptLoader` |
| `WorkflowDefinitionResolver` | Resolver | bound/effective Profile cascade, stage specs, structure, approval | `IWorkflowProfileProvider` |
| `WorkflowProfileProvider` (exists) | — | sole Profile collection authority | `WorkflowProfileRecordRow` + catalog |
| `MohistDefaultWorkflowProjection` (exists) | — | Issue↔WorkflowRun state projection | none (pure) |

\* Naming tension — see D3.

Each new type implements `IScopedService` so DI picks it up without manual
registration. Consumers swap the injected concrete type; route handler
signatures change the parameter type only.

### D3 — Suffix fit for the two merge/resolution units

`conventions.md` reserves `Resolver` for "external name → canonical resource"
(e.g. `ProjectResolver`), `Querier` for single-domain read projection, and
`Manager` for config/lifecycle policy. The variable merge and prompt resolution
are "resolve effective X for a run" — closer to read assembly than external-name
mapping. Two acceptable resolutions, to be confirmed at implementation time:

- Accept `Resolver` for these two because each resolves run context to a
  canonical effective resource (variables / prompt), and no other suffix fits a
  cross-store assembly. (Preferred — matches the existing `*Resolver` intent of
  producing one canonical result.)
- Alternative: fold each merge back into its resource's `Store` as a read
  method, avoiding a new type. Rejected because the merge is cross-scope
  (reads three Stores), so it would re-couple Stores; and the issue explicitly
  requires the merge to leave `WorkflowProfileManager` as a single-responsibility
  unit.

The Definition cascade stays a `Resolver` unambiguously: it resolves run context
to the canonical WorkflowDefinition.

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

### D5 — Legacy template CRUD retirement is gated on migration completeness

The Definition cascade still falls back to the legacy `ProjectWorkflowTemplates`
table (`LoadTemplateReferenceAsync` → `LoadProjectTemplateAsync`) for
issue-sourced and project-default resolution. `IssueGrain:318` reads the legacy
`GetDefaultTemplateAsync`. Retirement is sequenced:

1. Switch resolution + IssueGrain default read to `IWorkflowProfileProvider`
   (which already carries everything the migrator produced, including
   `DefaultWorkflowProfileId`).
2. Once no resolution path consults the legacy table, delete the template CRUD
   methods and the `ProjectWorkflowTemplates` read paths.
3. The `IssueWorkflowProfile.Template` inline column: the migrator already
   converts inline Issue definitions to collection Profiles
   (`issue-custom:...`); after confirmation, `IssueTemplateUpdateRequest.Template`
   custom-YAML handling is removed and Issue selection becomes a pure Profile-id
   reference.

The legacy tables/columns are left read-only until a separate persistence cleanup
confirms no consumer remains; this issue does not add an EF migration to drop
columns (Non-Goal), only removes the code paths.

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
6. **Retire legacy template CRUD** — D5 sequence; remove code paths.

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
- [`Resolver` suffix drift from the conventions definition] -> D3 calls out the
     tension and constrains the choice to two explicit options; the Definition
     cascade unambiguously fits, and the two merge units are the only ambiguous
     cases, documented rather than silently chosen.
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
the sole path before step D5 (retirement) lands. Rollback is `git revert` of the
relevant step, since each step leaves the suite green and introduces no schema or
behavior change. Steps may land across multiple PRs in D7 order; no step requires
another to have landed first except D5 step 2 after D5 step 1.

## Open Questions

- Does `MohistDefaultWorkflowProjection` stay a static class or become a DI
  service? It has no dependencies today, so static is fine; a service is only
  needed if a later change injects dependencies. Decide at step D6.
- Confirm `ProjectPromptTemplates` table (D4) has no external/migration reader
  before dropping the code path; if any startup upgrade reads it, retire the
  table in a follow-up rather than here.
- Whether the two `Resolver`-suffixed merge units (D3) are acceptable to
  conventions as written, or need an alternate suffix — confirm against
  `conventions.md` at implementation.
