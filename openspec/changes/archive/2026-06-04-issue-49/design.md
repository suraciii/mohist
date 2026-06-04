## Context

### Background

Mohist ships 12 hard-coded prompt templates (`proposal`, `specs`, `design`, `tasks`, `self-review`, `review`, `auto-fix`, `build`, `re-verify`, `review-self-check`, `explore`, `conflict-resolution`) as `.prompt` files under `packages/server/src/Mohist.Server/Workflow/Prompts/`. `IPromptLoader` / `FilePromptLoader` reads them at process start and returns a `Dictionary<string, string>` keyed by file name. `MohistDefaultIssueWorkflowProfile.BuildVariables` JSON-serializes that dictionary into the workflow `prompts` payload, and the runner's `renderTemplate` resolves `${{ prompts.xxx }}` references recursively.

This is fine for the default workflow, but operators cannot:

- Customize prompts per project (e.g. match a team's house style for `proposal`).
- Add new project-specific templates (e.g. `deploy-checklist`) without shipping a server change.
- See what a prompt looks like when the issue's `vars.*` are filled in, before it ships to an agent.
- Distinguish prompts by stage, tag, or description in any UI — the file name is the only metadata.

The proposal for #48 introduces `vars.*` as a 5-layer data layer; this issue introduces a parallel `prompts.*` as a 2-layer template layer. They compose at preview time (templates can reference any variable namespace), but storage and ownership are orthogonal.

### Current state

| Concern | Today | After this change |
| --- | --- | --- |
| Source of templates | Files in `Workflow/Prompts/*.prompt` | Files + DB rows |
| Metadata | None (file name is key) | YAML frontmatter (`name`, `description`, `tags`, `stage`) + DB columns |
| Scope layers | 1 (system) | 2 (system → project) |
| Override semantics | n/a | Whole body replacement (atomic) |
| UI | None | Settings → Templates tab |
| Preview | None | Server-side `PromptTemplateEngine` + `POST /preview` |
| Workflow start-work validation | None for prompt keys | Refuses with `400 missing_prompts` if a `prompts.*` reference is unresolved |
| Audit | None | `project_template_changed` / `project_template_deleted` events |

### Constraints

- The runner's `renderTemplate` is the source of truth for body expansion. We mirror its semantics on the server but do not change the runner.
- The `prompts` payload handed to the runner must remain a `Dictionary<string, string>` (runner reads strings, JSON-stringifies internally if a slot is non-string — but we keep them strings).
- Frontmatter parsing must tolerate files that have no frontmatter (existing 12 files do not, but we will add frontmatter to all of them in this change).
- The merge in `BuildVariables` must produce a stable order to keep `WorkflowVariablesRow` snapshots deterministic for diffing.
- All 648+ existing tests must continue to pass; the runner is untouched, so its tests do not need to change.

### Stakeholders

- **Operators / power users**: tune prompts per project without code changes.
- **Workflow authors**: add project-specific prompts (e.g. `deploy-checklist`) and reference them in `workflow.yaml`.
- **Agent operators**: preview the resolved body before letting the agent run.
- **Mohist core team**: keeps the system 12 templates as the source of truth; the frontmatter edits are reviewed like any source change.

## Goals / Non-Goals

**Goals:**

- Make `prompts.*` a first-class, project-overridable resource with metadata, CRUD, and UI.
- Add a server-side render engine that matches the runner's `renderTemplate` semantics (5-pass cap, missing-variable tracking, JSON-stringify for non-strings) so the editor's Preview pane faithfully shows what the agent will see.
- Validate `prompts.*` references at workflow start-work so a typo in a YAML key surfaces as a `400 missing_prompts` instead of an undefined token at agent time.
- Keep the runner untouched: it receives the merged `prompts` dictionary just like today, with the same `${{ prompts.xxx }}` semantics.
- Emit `IEventStore` events for every override upsert/delete so the existing Activity timeline shows who changed what.

**Non-Goals (v1):**

- Issue-level template overrides.
- Template variable type-checking / parameter signatures.
- Template version history, diff view, or rollback.
- Shared cross-project template library.
- Tag-based filtering as a first-class taxonomy.
- Bulk import / export.
- Editing the system `.prompt` files through the UI (they remain source-controlled).
- Renderer/parser that diverges from the runner's `renderTemplate` semantics. If the runner changes, both sides change together in a single follow-up.

## Decisions

### Decision 1: 2-layer scope (system file + project DB) instead of 1 or 3 layers

We chose 2 layers for v1: a read-only system layer (`.prompt` files) and a project DB layer. The proposal explicitly defers issue-level overrides to v2.

**Rationale.** The use case in the proposal ("tune `proposal` to a team's voice", "add a `deploy-checklist`") is unambiguously project-scoped. Issue-level overrides are an edge case ("this one issue wants a different prompt") that has not been requested and would significantly expand the resolution matrix and UI complexity. Keeping it to 2 layers also keeps the merge in `BuildVariables` trivial and side-effect free.

**Alternatives considered.**

- *Single layer (DB only)*: makes system templates a DB seed step, complicates upgrades, and requires operators to ship `INSERT` statements to change defaults. Rejected.
- *3 layers (system → project → issue)*: the issue layer would multiply storage and UI surface without a known user. Deferred to v2 if and when it earns its keep.

### Decision 2: YAML frontmatter via YamlDotNet with explicit tolerance for missing/partial

Each `.prompt` file gets an optional `---`-delimited YAML header with `name`, `description`, `tags` (list of strings), `stage` (string). `PromptFrontmatterParser` is a tiny YamlDotNet wrapper. The parser produces a `PromptFrontmatter` record and a `Body` string; the body is always the file content with the frontmatter block removed.

**Rationale.** YamlDotNet is already in `Directory.Packages.props` (used by `WorkflowYamlSerializer`). Tolerating missing frontmatter matters because (a) a 3rd-party prompt could be added to the directory without a header, (b) the loader has to stay usable for the existing 12 files in the moment between "refactor loader" and "add frontmatter to all 12". We choose explicit defaults (`name = key`, `description = ""`, `tags = []`, `stage = null`) over throwing, but we still throw on malformed YAML so a typo does not silently load a half-parsed body.

**Alternatives considered.**

- *Hard requirement on frontmatter*: rejected — forces a coordinated edit and breaks any 3rd-party `.prompt` drop-in.
- *Parse frontmatter manually with `string.Split`*: rejected — re-implements YAML for no upside and YamlDotNet is already in the project.
- *Store metadata in a sidecar `.json` file*: rejected — splits one logical artifact into two files, easy to drift.

### Decision 3: Atomic body replacement, no deep-merge

`ProjectTemplateRow.Body` is a single string. A project override fully replaces the system body; the engine does not try to merge sections or interpolate one into the other. The merge in `BuildVariables` is therefore a flat `Dictionary<string, string>` with project rows winning on the same key.

**Rationale.** Bodies are not structured documents — they are free-form Markdown with embedded `${{ ... }}` tokens. A "deep merge" would require picking a structural unit (headings? `<artifact>` tags?), each of which is wrong for at least one of the 12 existing prompts. A user who wants partial inheritance is best served by an explicit "Copy to override" button in the UI (which pre-fills the editor with the system body).

**Alternatives considered.**

- *Deep-merge by `<task>`, `<dependencies>`, etc.*: rejected — couples the merge to the artifact XML schema and gives surprising results when the system adds a new section.
- *String interpolation override* (treat the override body as a template that receives the system body as a variable): rejected — adds a second interpolation layer with its own escape rules; users do not ask for it.

### Decision 4: `PromptTemplateEngine` is a C# port of runner `renderTemplate`, not a divergence

The engine mirrors the runner's contract exactly:

- 5-pass cap on `${{ path }}` expansion.
- Unresolvable references left in place; path recorded in `missingVariables`.
- Non-string values are JSON-stringified.
- Whitespace handling around the `{{`/`}}` markers matches the runner regex.
- `ExtractVariables` is a static regex scan over the body (no rendering, no recursion), producing a sorted, deduplicated path list.

**Rationale.** The Preview pane must show exactly what the runner will hand the agent. If the two engines diverge, the preview lies. The port is small (~80 LoC plus a regex) and is fully unit-tested against a fixture of inputs the runner already handles.

**Alternatives considered.**

- *Call into the runner over a process boundary*: rejected — adds inter-process cost, a transport layer, and a startup dependency. The whole point of preview is "show me now".
- *Share code via WebAssembly or source generator*: rejected — pulls in tooling for ~80 LoC.

### Decision 5: Workflow start-work validates `prompts.*` and rejects with 400 + `missing_prompts`

`MohistDefaultIssueWorkflowProfile` exposes the merged `prompts` dictionary (or `IPromptLoader` + `IProjectTemplateStore` are queried directly inside `IssueGrain.StartWorkAsync`). The set of referenced keys is extracted by a regex scan over the workflow YAML's `prompt:` values (or, more precisely, over `definition.Variables` and the `with.prompt` of every task). Any reference not in the merged map produces a `400 missing_prompts` with `details.missingKeys`.

**Rationale.** A typo (`prompts.proposa1`) is currently caught only by the agent seeing an unresolved `${{ prompts.proposa1 }}` token in its prompt, which silently degrades quality. Catching it at start-work costs one regex scan per start and turns a silent failure into a 400 with a remediation message.

**Alternatives considered.**

- *Lazy resolution + warning event*: rejected — warnings get ignored; an explicit fail-fast is more discoverable.
- *Re-scan at every dispatch*: rejected — too expensive and the validation does not depend on the variable values, only on the key set.

### Decision 6: 8 REST endpoints, 5 web hooks, audit via `IEventStore`

The API surface mirrors the proposal verbatim:

| Endpoint | Verb | Purpose |
| --- | --- | --- |
| `/api/templates/system` | GET | 12 system templates with frontmatter |
| `/api/projects/{id}/templates` | GET | Effective (system + project) list with `source` |
| `/api/projects/{id}/templates/{key}` | GET | Single effective template |
| `/api/projects/{id}/templates/{key}/override` | GET | 200 if overridden, 404 otherwise |
| `/api/projects/{id}/templates/{key}/override` | PUT | Upsert override (audited) |
| `/api/projects/{id}/templates/{key}/override` | DELETE | Remove override, idempotent (audited) |
| `/api/projects/{id}/templates/{key}/preview` | POST | Server-side render |
| `/api/templates/extract-variables` | POST | Static variable scan, no project context |

The web layer collapses this into 5 hooks (`useProjectTemplates`, `useProjectTemplateOverride`, `useUpsertProjectTemplateOverride`, `useDeleteProjectTemplateOverride`, `usePreviewProjectTemplate`) and one direct fetch for `system` templates.

`PUT`/`DELETE` append `project_template_changed` / `project_template_deleted` to `IEventStore` with `payload = { key, before, after, source: "user" }`. They surface in the existing Activity timeline with no new event-type registrations because the timeline already shows everything in the events table.

**Rationale.** Splitting read / override / preview into separate routes is a small REST cost for a clean permission story: GETs are safe and cacheable, override routes are explicitly CRUD, preview is a derived view that can be rate-limited independently. The 5-hook count is the minimum that lets the list, editor, and new-template dialog each hold their own cache key.

**Alternatives considered.**

- *Single `/api/projects/{id}/templates` with all verbs*: rejected — the override is conceptually a sub-resource and gets its own URL; the 404 semantics are cleaner.
- *GraphQL or RPC*: rejected — REST is the project's convention.

### Decision 7: Settings → Templates tab as a new tab (6th in the existing 5-tab Settings)

The tab sits between `Workflows` and `System` (or after `System` — placement is a UX detail settled in the spec; the implementation is tab-agnostic). The list view, editor, and New Template dialog are three sibling routes under `/settings/templates/...` so deep links work and back/forward navigates cleanly.

**Rationale.** Templates are a project-scoped resource, so the tab renders the existing "No project selected" placeholder when no project is active. Following the existing Settings routing convention (path-based tab key) keeps the diff in `SettingsPage.tsx` to ~5 lines and avoids a router upgrade.

**Alternatives considered.**

- *Modal overlay on top of the existing Workflows tab*: rejected — templates are a first-class concern; sharing the tab buries them.
- *Side menu under Project*: rejected — does not match the existing Settings pattern.

## Risks / Trade-offs

- **[Frontmatter drift]** A `.prompt` file edited to add a `stage: plan` could suddenly match the workflow's stage filter, changing which prompts the agent "sees" in a list view. -> The frontmatter is metadata, not a filter; we do not filter prompts by stage in the API. Stage is a display badge only.

- **[Override copies a stale system body]** If the system `proposal.prompt` changes after a user copies it into an override, the override silently diverges. -> The UI surfaces the `source: "projectⓘ"` label with a hover hint that explains the override replaces the system body. We do not surface a "system body is newer" warning in v1; if that proves important, a v2 follow-up can add a hash column and a "system has updated" indicator.

- **[5-pass cap is not configurable]** If a future prompt legitimately needs 6 passes, the engine silently leaves the 6th `${{ ... }}` unexpanded (matching the runner). -> Document the cap in the engine's XML doc comment. Raising the cap is a single constant change in two places, kept coordinated.

- **[Project override doubles the storage of every prompt body]** A 12-template system + 12 overrides = 24 bodies in SQLite. -> Bodies are a few KB each; this is negligible. If it ever becomes a problem, gzip in `Body` is a transparent change.

- **[Engine must match runner exactly]** If the runner's `renderTemplate` changes (say, supporting `${{ (expr) }}` filters), the server's `PromptTemplateEngine` would silently diverge. -> A fixture-driven cross-test in `PromptTemplateEngineSpecs` runs the runner in-process (or replays a fixed set of inputs) and asserts parity. The test file is a contract; both engines are expected to pass it.

- **[Preview pane hits the server on every keystroke]** Each edit of the variables JSON triggers a `POST /preview`. -> The hook debounces (250 ms) and the request is cheap (no DB, no IO). If we add a 1MB body cap to `PromptTemplateEngine`, we cover pathological inputs without rate-limiting.

- **[Race between override delete and an in-flight workflow start]** A delete between "validate keys" and "build variables" could leave the workflow using a different body than the one validated. -> `BuildVariables` re-reads the merged map atomically inside `StartWorkAsync`; the validation and the variable build see the same snapshot because both go through the same store call. The window is the store's per-request consistency, not a TOCTOU race across requests.

- **[Migration touches a live table]** The migration uses `CREATE TABLE IF NOT EXISTS` and is idempotent (matches `20260601021500_AddWorkflowStageLocks.cs`). -> Re-running on an up-to-date DB is a no-op. The `Index` is created with the same `IF NOT EXISTS` guard.

- **[Five new web hooks add to a 648+ test suite]** Every hook is unit-tested with MSW and the section/editor have integration tests; CI cost grows linearly with hook count. -> Acceptable. The 5 hooks all follow the same pattern as `useRepositories` / `useAddRepository`, so the test scaffolding is reusable.

## Migration Plan

### Deploy

1. **Schema first.** Apply migration `20260601050000_AddProjectTemplates` (idempotent `CREATE TABLE IF NOT EXISTS` + index). The migration runs on silo startup via `MohistDatabaseMigrator`. No data backfill is needed because `ProjectTemplates` starts empty.
2. **Server binary.** Deploy the new `Mohist.Server` build. The new `IPromptLoader` shape is backwards compatible: `FilePromptLoader.LoadAll()` now returns the same `Dictionary<string, string>` it did before, sourced from `SystemTemplate.Body` instead of the raw file content. The `LoadAll` contract is unchanged. `IProjectTemplateStore` is registered as a new DI service — no existing call site breaks.
3. **Frontmatter on the 12 system files.** Ship the 12 `.prompt` files with their frontmatter as part of the same binary. The loader tolerates files without frontmatter, so partial rollouts (old binary, new file content) are safe in either direction.
4. **Web build.** Deploy the new `packages/web` bundle. The new tab is hidden behind a check on `useProject()` — if the templates API returns 404 (server not yet updated), the section shows a "Templates not available" placeholder rather than crashing.
5. **Audit events.** Add `project_template_changed` and `project_template_deleted` to `EventBusEventTypes.All` in the same change. Existing timeline consumers see them as new event types; the timeline already supports unknown types.

### Rollback

1. **Server binary rollback.** Revert `Mohist.Server` to the previous build. `IPromptLoader`/`FilePromptLoader` reverts to the raw-file loader. The `ProjectTemplates` table is left in place; it is read by nothing.
2. **Web binary rollback.** Revert `packages/web` to the previous build. The Templates tab is gone; no client code holds a reference to its hooks.
3. **Schema rollback (only if downgrading permanently).** Manually drop the `ProjectTemplates` table and its index. There is no production data in it yet during the first rollout. (For follow-up rollbacks after a release that wrote rows, dump the rows to JSON first.)

The 5-pass `prompts.*` validation in start-work is a *behavior* change. A workflow that happens to reference a now-unknown key would start failing. To make this safe:

- The validation only runs when the merged map is missing the key. Today the merged map = system loader output, which is the same 12 keys the workflow YAML references. So no key is newly missing in the first release.
- If we add a new system key in the future and a workflow is already running with the old workflow.yaml, the *workflow's* prompt references are validated against the *current* merged map. New keys are additive, so existing references stay resolvable.
- A `unknown_prompt_key` audit event is emitted alongside the 400 so an operator can find the offending workflow.

### Compatibility

- Runner: zero changes. The 12 `.prompt` files keep the same body content (frontmatter is added on top, the body is what was already there). `${{ prompts.xxx }}` resolves identically.
- Existing workflow YAMLs: continue to work because the merged map is a superset of the system loader output.
- Existing API clients: no breaking change. New routes are additive.

## Open Questions

- **Placement in Settings tab order.** `templates` after `workflows` (closer to the engine) or after `system` (visually last, as a "user customization" surface)? Settled in the spec file (templates is a new top-level tab in the existing 5-tab Settings page); not a design-blocker.

- **Should the override PUT return the effective template or the override row?** The spec says "the stored row", which is the most truthful answer. The web client derives the effective row in a follow-up GET to refresh `source`. We may revisit if list refresh latency becomes a UX problem.

- **Should `ExtractVariables` recursively resolve `vars.*` values in the body to find indirect references?** No — it is a static scan only. The runner's `renderTemplate` handles runtime resolution. We deliberately do not implement a second-pass resolver on the server.

- **Should we deduplicate identical bodies across project rows?** Not in v1. Storage is cheap; deduplication complicates the audit story (before/after diffs become per-row rather than per-key).

- **How does this interact with #48's effective vars endpoint?** The Preview pane defaults to a representative sample in v1. When #48 ships `GET /api/issues/{n}/vars/effective`, the editor can swap the sample for the real vars via a button. The engine itself does not need to change.

- **Should system template `stage` drive list filtering?** The spec says no — stage is metadata for display, not a filter. We can revisit if operators ask for "show me only plan-stage prompts" as a first-class view.
