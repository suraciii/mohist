## Why

Prompt templates today are read-only `.prompt` files in source control, with no metadata, no project-level customization, and no UI. Operators cannot tune workflow prompts per project (e.g. adapt `proposal` to a team's voice) or add project-specific templates (e.g. `deploy-checklist`) without shipping a code change. This blocks the natural follow-up to #48 — once `vars.*` is a real data layer, users need a parallel way to manage `prompts.*` as a first-class resource, including previewing the resolved body against effective vars before it ships to an agent.

## What Changes

- Add YAML frontmatter (name, description, tags, stage) to all 12 system `.prompt` files; parse it via `YamlDotNet` so files without frontmatter still load.
- Introduce a 2-layer template model: **L0 system** (file, read-only) + **L1 project** (DB row, user-managed). A project row with the same key fully overrides the system body; project-unique keys add new templates. Issue-level overrides are deferred to v2.
- Introduce `ProjectTemplateRow` + EF migration `20260601050000_AddProjectTemplates` (PK `ProjectId, Key`) and a `IProjectTemplateStore` for CRUD.
- Introduce a C# `PromptTemplateEngine` (5-pass `${{ path }}` resolution, missing-variable tracking, JSON-stringify of non-strings) plus a static `ExtractVariables` regex scan.
- Extend `MohistDefaultIssueWorkflowProfile.BuildVariables` to merge system templates and project overrides into the `prompts.*` payload, preserving runner's existing `renderTemplate` semantics. Runner changes: **none**.
- Add 8 REST endpoints under `/api/templates/system` and `/api/projects/{id}/templates/...` (system list, effective list, effective single, override GET/PUT/DELETE, preview POST, extract-variables POST). PUT/DELETE emit `project_template_changed` / `project_template_deleted` audit events.
- Reject workflow start-work that references an unknown `prompts.*` key with HTTP 400 + missing key list.
- Add a new top-level tab **Settings → Templates** in Web UI (alongside the existing 5 Settings tabs: Coder Agent, Runtime, Repositories, Workflows, System): list view (search, source label, stage badge, action buttons), two-pane editor (metadata + body on left, live preview + variable checklist on right), and a New Template dialog. Web API: 5 spec-mandated hooks plus 2 supporting hooks (system list, extract-variables) for the new endpoints.
- Invariants: read-only system; immutable key (rename = delete+create); body is atomic, no deep-merge; max 5 interpolation passes; PK enforces project uniqueness; missing frontmatter tolerated with defaults.

## Capabilities

### New Capabilities

- `prompt-template-management`: Server-side prompt template system. Covers YAML frontmatter format, the 2-layer system+project model, `ProjectTemplateRow` storage, `IProjectTemplateStore`, the C# `PromptTemplateEngine` (render + extract), the 10 REST endpoints, the workflow `prompts.*` merge in `BuildVariables`, and the audit events `project_template_changed` / `project_template_deleted`. Becomes `specs/prompt-template-management/spec.md`.
- `prompt-template-editor`: Web UI for managing prompt templates. Covers the Settings → Templates tab (list, editor two-pane, new template dialog, preview), the 5 web API hooks, and source-label / stage-badge / variable-availability display. Becomes `specs/prompt-template-editor/spec.md`.

### Modified Capabilities

- `workflow-config`: Requirement on how `prompts.*` references in `workflow.yaml` are resolved changes from "system files only" to "system + project merge, unknown key fails start-work with 400". The file format itself is unchanged.
- `web-ui`: Settings page gains a Templates tab alongside Project/System; tab routing and permissions follow existing Settings conventions.

## Impact

- **Server (C#)**: 12 `.prompt` files get frontmatter; new files under `Prompts/Domain`, `Prompts/Infrastructure`, `Prompts/Storage`; new EF migration; new `Api/ProjectTemplateRoutes.cs` and `Api/TemplateRoutes.cs`; refactor of `FilePromptLoader` / `IPromptLoader` to return `SystemTemplate`; `MohistDefaultIssueWorkflowProfile` gains the project-overrides merge; `MohistServiceRegistration` adds `IProjectTemplateStore` DI.
- **Runner**: none. `${{ prompts.xxx }}` resolution and `renderTemplate` are unchanged; the server just hands it a richer `prompts` dictionary.
- **Web**: new `entities/template/` directory with 5 hooks + model types; new `pages/settings/ui/TemplatesSection.tsx`, `TemplateEditor.tsx`, `NewTemplateDialog.tsx`; `SettingsPage.tsx` adds the Templates tab.
- **Tests**: new server spec files `PromptTemplateEngineSpecs`, `PromptFrontmatterParserSpecs`, `ProjectTemplateRoutesSpecs`; refactor of `MohistDefaultWorkflowProfileSpecs` to cover the merge; new web tests `TemplatesSection.test.tsx` and `TemplateEditor.test.tsx`. Existing 648+ tests must continue to pass.
- **Dependencies**: adds `YamlDotNet` to `Mohist.Server` for frontmatter parsing.
- **Audit**: reuses the existing `IEventStore` and Activity timeline; no new timeline surface.
- **Relationship to #48**: #48 owns `vars.*` (5-layer merge); this issue owns `prompts.*` (2-layer override). They are orthogonal in storage but compose at preview time — once #48 ships, the template editor's Preview pane can pull effective vars via `GET /api/issues/{n}/vars/effective`.
