## Context

Mohist manages two orthogonal, project-level configuration dimensions today: **workflow profiles** (decide *how* an issue executes) and — soon — **issue templates** (decide *what* an issue body looks like). Today there is exactly one hardcoded three-voice PRD writing convention, embedded in the `mohist-explore` skill (`packages/cli/Mohist.Cli/skill-data/mohist-explore/`), with no project-level way to define or select issue body conventions.

The issue template system is specified as a **mirror** of the existing workflow profile model. The workflow profile side already implements every concept we need, so this change largely follows an established pattern rather than inventing new architecture:

- `IssueWorkflowProfiles.DefaultId = "mohist/default"` → built-in default id.
- `IIssueWorkflowProfile` (`Id`, `DisplayName`, `Description`, `IsDefault`, `SuitableFor`, `Definition`) → template model shape.
- `IssueWorkflowProfileRegistry` (in-memory built-ins, `Get/List/ListDescribed/Default/Exists`) → template registry.
- `ProjectWorkflowTemplateRow` (DB, key `ProjectId + TemplateId`) → project custom storage.
- `GET /api/workflow-profiles` (in `SystemRoutes.cs`) → read endpoint.
- CLI `IssueCommands` subcommand group → `mo issue template` subcommand group.
- Web `useWorkflowProfiles` hook + `CreateIssueDialog` selector → `useIssueTemplates` + template selector.

**Stack:** Server is .NET (ASP.NET Core minimal APIs, EF Core/SQLite, Orleans), organized by domain (`Issue/`, `Workflow/`, `Project/`, each with `Domain/Services/Grains`) and API-by-file (`Api/*Routes.cs`). CLI is a .NET global tool whose `MohistCliApi` HTTP client talks to the server and resolves the active project via `ResolveProjectIdAsync`. Web is React + TanStack Query, FSD-ish (`entities/`, `features/`, `widgets/`).

**Constraints:**
- Purely additive — must not change the existing issue schema, body frontmatter, or workflow profile behavior (see `issue-body-frontmatter`, `explore-issue-handoff` capabilities — both untouched).
- MVP scope is **list / get + select** only; no CRUD UX, no disable UI, no cross-project sharing, no `issue type` concept (issue Non-Goals).

## Goals / Non-Goals

**Goals:**
- Introduce the Issue Template data model (frontmatter + ordered `sections` with `guidance`/`placeholder`) as a project-scoped resource.
- Ship the built-in default `mohist/default` (5 sections in fixed order, guidance sourced from the `mohist-explore` skill) as in-binary data that is always available.
- Expose list/get via API, CLI, and a Web UI selector that prefills the body skeleton.
- Make template `suitable_for`/`isDefault` symmetric with workflow profiles (same matching semantics).
- Support project custom templates at the data layer; allow disabling the default at the data layer.

**Non-Goals:**
- No CRUD UX (editor / preview / versioning) — only list/get + select.
- No disable/override UI — data-layer support only.
- No automated template-by-context routing / `issue type` concept.
- No cross-project template sharing or inheritance.
- No change to the `mohist-explore` / `mohist` skills themselves (the skill is only a *data source* for the default guidance).

## Decisions

### Decision 1: Storage — DB table for project customs + disable flag; in-binary for the default

Project custom templates and the disable flag are stored in SQLite via EF Core, mirroring `ProjectWorkflowTemplateRow` (`Infrastructure/Data/Workflow/`). The built-in `mohist/default` is **in-binary** (a static C# structure), mirroring `MohistDefaultIssueWorkflowProfile` + `IssueWorkflowProfiles.DefaultId`.

New pieces:
- `Issue/Services/IssueTemplates/IssueTemplates.cs` — `public const string DefaultId = "mohist/default";`
- `Issue/Domain/IssueTemplate/` — `IIssueTemplate` (`Id`, `Name`, `About`, `IsDefault`, `SuitableFor`, `Defaults`, `Sections`), `IssueTemplateSection` (`Title`, `Guidance`, `Placeholder`), `IssueTemplateDefaults` (`Labels`, `Risk`, `Workflow`).
- `Issue/Services/IssueTemplates/MohistDefaultIssueTemplate.cs` — the in-binary default with the 5 sections; guidance text transcribed from the `mohist-explore` skill (`SKILL.md` Workflow steps + `references/issue-body-template.md`).
- `Issue/Services/IssueTemplates/IssueTemplateRegistry.cs` — mirror of `IssueWorkflowProfileRegistry`: holds built-in default, merges project customs, applies disable filter, exposes `Get/List/ListDescribed/Default/Exists`.
- `Infrastructure/Data/Issue/ProjectIssueTemplateRow.cs` — `ProjectId + Name` key, `Template` JSON column (mirror of `ProjectWorkflowTemplateRow`).
- Disable flag stored on the project's existing profile/settings row (a nullable/JSON column) — data-layer only, no UX.

**Alternatives considered:**
- *Project directory files* (`.mohist/issue-templates/*.yaml`, like GitHub). Rejected for MVP: the existing project-resource pattern (workflow templates) is DB-backed, and a DB table keeps list/get, project scoping, and disable uniform. The spec explicitly leaves the entry mechanism open (config file *or* API), so a later config-file importer can feed the same table without changing the read model.
- *Packaged YAML/markdown loaded at startup* (like the prompt loader). Rejected for the default: in-binary keeps the default version-aligned with the binary and needs no loader; guidance is static.

### Decision 2: API namespace — distinct `IssueTemplateRoutes.cs` at `/api/issue-templates`

A new `Api/IssueTemplateRoutes.cs` exposes:
- `GET /api/issue-templates` → list (project-scoped via the active project / `ProjectResolutionEndpointFilter`), returns `IssueTemplateInfo` (`Name`, `About`, `SuitableFor`, `IsDefault`, `Source`).
- `GET /api/issue-templates/{name}` → full template incl. `sections` with `guidance`/`placeholder`.

This is deliberately **not** added to the existing `Api/TemplateRoutes.cs`, which already owns prompt templates (`/api/templates/system`, `/api/templates/extract-variables`) — a different concept. It is also not nested under `/api/workflow-*`. The endpoint resolves the active project the same way other project-scoped reads do (the dialog and CLI always operate with an active project), so the disable filter and project customs are applied server-side.

**Alternatives considered:**
- *Nest under `/api/projects/{id}/issue-templates`.* Rejected: the rest of the read surface (`/api/workflow-profiles`, etc.) resolves the active project from context rather than a path param; consistency wins.
- *Reuse `TemplateRoutes.cs`.* Rejected: name/purpose collision with prompt templates.

### Decision 3: `suitable_for` is shared, not duplicated

`IIssueTemplate.SuitableFor` is typed `IReadOnlyList<string>` — identical to `IIssueWorkflowProfile.SuitableFor`. The matching helper that `explore-issue-handoff` uses to match context against `suitable_for` is reused unchanged, satisfying "same matching semantics". `IsDefault` follows the workflow profile default-selection rule (default wins its `suitable_for` context, ordered with default first — same as `IssueWorkflowProfileRegistry.List`).

No automated template-by-context selection ships in MVP (that would imply the out-of-scope `issue type` concept); we only guarantee the *data* and *matching* are symmetric so a later caller can use them.

### Decision 4: Body skeleton prefill is a pure client-side transform

When a template is selected in `CreateIssueDialog`, the body skeleton is composed **client-side** by joining `## {section.title}\n{section.placeholder}` in section order. `guidance` is never sent to the body. The server returns `sections` (with `guidance` for the `get`/detail view and CLI `get`), but the Web prefill only consumes `title` + `placeholder`. No server-side rendering of the skeleton is needed.

**Alternatives considered:**
- *Server renders the skeleton string.* Rejected: trivial transform, and keeping it client-side avoids a second representation of "the body" that could drift from section order.

### Decision 5: CLI — `mo issue template` subcommand group

Add a `template` subcommand to `IssueCommands` (`MohistCliCommands.Issue.cs`), with `list` and `get <name>` children, consuming `/api/issue-templates` via `MohistCliApi` and resolving the project via `ResolveProjectIdAsync` (exactly as `mo issue list` does). Output uses the existing `TableRenderer`.

**Alternatives considered:**
- *Top-level `mo template`.* Rejected: the issue explicitly specifies `mo issue template`, and it groups naturally with issue creation.

### Decision 6: Web — mirror the workflow profile hook + add a selector

Add `entities/issue-templates` (types + `useIssueTemplates`/`useIssueTemplate` TanStack Query hooks hitting the new endpoints), mirroring `entities/settings`'s `useWorkflowProfiles`. Add a `TemplateSelector` inside `features/create-issue/ui/CreateIssueDialog.tsx` (next to the existing `useWorkflowProfiles`-driven controls) that populates from `useIssueTemplates` and, on select, sets the body state to the composed skeleton.

## Risks / Trade-offs

- **[Default guidance drifts from the `mohist-explore` skill]** → The default's `guidance` is hand-transcribed from the skill into an in-binary C# structure. Since this issue's Non-Goal forbids modifying the skill, the two are not auto-synced. Mitigation: keep a source-pointer comment in `MohistDefaultIssueTemplate.cs` naming the skill sections transcribed; defer automated sync to a follow-up issue. Acceptable for MVP because the skill changes rarely.
- **[Route-name confusion with prompt `TemplateRoutes`]** → Mitigated by Decision 2 (separate `IssueTemplateRoutes.cs` + `/api/issue-templates` namespace).
- **[Two "template" concepts in the codebase (workflow-definition templates vs issue-body templates)]** → Naming risk. Mitigation: consistently prefix the new concept as `IssueTemplate` / `issue-templates` everywhere (domain, DB row `ProjectIssueTemplateRow`, route, CLI). Document the distinction in the new files' XML doc comments.
- **[`defaults` (labels/risk/workflow) are advisory but not applied in MVP]** → The model carries `defaults`, but MVP only prefills the *body skeleton*; whether `defaults` auto-populate the create-issue form fields is left to implementation and may slip to a follow-up. Trade-off documented to avoid scope creep.
- **[DB migration adds a table]** → Purely additive; see Migration Plan.

## Migration Plan

1. **Schema:** Add an EF Core migration creating the `project_issue_templates` table (columns: `project_id`, `name`, `template` JSON, `created_at`, `updated_at`; composite PK `project_id + name`), plus the disable flag column on the existing project profile/settings table. Register the new `DbSet<ProjectIssueTemplateRow>` in `MohistDbContext`.
2. **Built-in default:** No migration needed — `mohist/default` is in-binary and resolves through `IssueTemplateRegistry` regardless of DB state.
3. **Deploy:** Server startup applies the migration automatically (existing EF Core behavior). No data backfill (purely additive; existing projects simply gain `mohist/default`).
4. **Rollback:** Revert the binary; the extra table/column is inert (no existing read path depends on it). To fully clean, drop `project_issue_templates` and the disable column — no data loss in pre-existing issue/workflow data.

No breaking API/CLI changes; existing clients are unaffected because all additions are new endpoints/commands.

## Open Questions

- **Disable storage shape:** column on `ProjectWorkflowProfile` vs a dedicated project-settings row — pick at implementation time; either satisfies the data-layer requirement.
- **`defaults` application scope:** does MVP apply `defaults.labels/risk/workflow` to the create-issue form on template select, or only prefill the body skeleton? Spec/AC center on the body skeleton; applying defaults is a candidate for a fast-follow. Resolve during implementation.
- **Custom template entry UX:** confirmed out of scope for MVP (data-layer only). The first concrete entry path (API vs config-file importer) to be picked when CRUD is scheduled in a later issue.
