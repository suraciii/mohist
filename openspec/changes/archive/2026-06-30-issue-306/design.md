## Context

Mohist ships a single built-in issue template (`mohist/default`) hardcoded as a C# class
(`MohistDefaultIssueTemplate.cs`). Bug and Refactor issues are forced to wear the Feature
PRD and fabricate a User Voice. Loading is not layered — `IssueTemplateRegistry.List()`
materializes every template's full body just to render a directory. Template metadata
(`suitableFor`, `defaults`, `isDefault`, `about`) never drives selection, because choosing a
template is a judgment an agent or human makes by reading the description, not a programmatic
match (`registry.Matches()` has zero production callers — only tests reference it).

Current state of the code:

- `IssueTemplateRegistry` holds one hardcoded builtin (`MohistDefaultIssueTemplate`) plus
  custom templates deserialized from `ProjectIssueTemplates` DB rows (`IssueTemplateDto`).
- `IIssueTemplate` exposes `Id/Name/About/IsDefault/SuitableFor/Defaults/Sections`.
- `IssueTemplateInfo` and `IssueTemplateDetail` (the API records) mirror those fields.
- `IssueTemplates.DefaultId = "mohist/default"`.
- `SuitableForMatcher.Matches()` is invoked on **two** paths: templates
  (`IssueTemplateRegistry.Matches`, line 157) and workflow profiles
  (`IssueWorkflowProfileRegistry`, line 51). Only the template path is removed.
- The three asset files already exist at
  `Issue/Services/IssueTemplates/templates/{feature,bug,refactor}.md` (frontmatter =
  `name` + `description`, body = `## Section` + `<!-- guidance -->` + `<placeholder>`).
  They are the content source of truth; this change wires them up.
- The csproj already uses `CopyToOutputDirectory="PreserveNewest"` for `*.workflow.yaml` and
  `*.prompt`; the same mechanism is extended to `templates/*.md`.

Constraints: server-only change; no DB schema migration (built-ins are file assets, custom
rows untouched); `TreatWarningsAsErrors` enforces the build so every consumer must move
in lockstep.

Stakeholders: Web create-issue dialog (reads list/detail), CLI `mo issue template
list|get`, and the (manual, out-of-scope) `mohist-create-issue` skill.

## Goals / Non-Goals

**Goals:**

- Three built-in templates (Feature/Bug/Refactor) loaded from `templates/*.md` at runtime.
- Two-stage loading: `list` parses **frontmatter only**; `get` parses frontmatter + body.
- Trim template metadata to exactly `name` + `description` across server records, HTTP
  response, CLI, and Web.
- Remove programmatic template matching (`registry.Matches()`); keep the workflow-profile
  `Matches()` path unchanged.
- Supersede `mohist/default` with `feature`, with a compat alias so existing references
  keep resolving.
- Delete `MohistDefaultIssueTemplate.cs`.

**Non-Goals:**

- Rewrite the `mohist-create-issue` skill to consume `list`/`get` (manual, non-code).
- Surface `description` in the Web create-issue dropdown (deferred).
- Custom-template CLI/API write path (separate issue).
- DB schema migration for custom template rows.
- Per-template default workflow/risk (those are issue frontmatter, not template metadata).

## Decisions

### D1. File loader mirrors skill discovery; reuse `PromptFrontmatterParser`

A new loader scans `templates/*.md` (filename → template id). Discovery reads frontmatter
only; detail reads frontmatter + body. Both tiers reuse `PromptFrontmatterParser`
(`Workflow/Services/Prompts`) for frontmatter, matching the proposal and the existing
`SkillAssetService.TryReadFrontmatter` discovery pattern.

- **Alternative considered:** a local frontmatter reader (like the CLI's hand-rolled
  `TryReadFrontmatter`). Rejected — duplicates parsing logic the proposal explicitly wants
  reused.
- **Trade-off:** `PromptFrontmatterParser` returns a workflow-typed `PromptFrontmatter`
  (`Name/Description/Tags/Stage`). Issue templates only need `Name`+`Description`; `Tags`
  and `Stage` are ignored. This is a minor type leak across feature boundaries. Accepted
  now (DRY wins); if a third consumer appears, generalize the parser into a shared
  frontmatter primitive.

### D2. Body → sections parser for the detail tier

The detail tier must return `IssueTemplateSection(Title, Guidance, Placeholder)` so the Web
`composeIssueTemplateBody` (which joins `## {title}\n{placeholder}` and deliberately
excludes `guidance`) keeps working unchanged. A small parser splits the body on `## `
headers; within each section a leading `<!-- ... -->` is the `Guidance`, the remainder is
the `Placeholder`. This preserves the existing section shape, minimizing Web churn.

- **Alternative considered:** return the raw body string and drop the section abstraction.
  Rejected — it would force a Web rewrite of `composeIssueTemplateBody` and the detail
  rendering, expanding scope past "metadata trim".

### D3. Metadata trimmed to `name` + `description`; `about` → `description`

`IIssueTemplate` loses `About/IsDefault/SuitableFor/Defaults`. `IssueTemplateInfo` becomes
`(Id, Name, Description, Source)`; `IssueTemplateDetail` becomes
`(Id, Name, Description, Sections, Source)`. `about` is renamed `description` (not dropped)
because it carries the selection signal. Sorting in `List()` drops the `IsDefault` key and
sorts by `Id`.

### D4. Remove `registry.Matches()`; keep `SuitableForMatcher` for profiles

`IssueTemplateRegistry.Matches()` (line 156-157) and the `SuitableFor` field it reads are
removed. `SuitableForMatcher` **stays** — `IssueWorkflowProfileRegistry.cs:51` still uses
it on the workflow-profile path, which is unchanged. Only the template-side call site and
the test that exercises it are removed.

### D5. `mohist/default` → `feature` via a compat alias

`IssueTemplates.DefaultId` becomes `"feature"`. The registry accepts `mohist/default` as an
alias that resolves to the `feature` template (same object / identical response). Alias
resolution lives in `Get`/`Exists`, so every read surface (HTTP get, `registry.Get`,
`registry.Exists`) inherits it transparently. The CLI help text example is updated to
`feature`.

- **Alternative considered:** a one-shot data migration rewriting stored `mohist/default`
  references. Rejected — the alias is cheaper, non-breaking, and the write path is out of
  scope.

### D6. Custom DB templates: tolerant deserialization, no migration

Custom templates are stored as `IssueTemplateDto` JSON (old shape with `About`/`SuitableFor`/
`Defaults`). `ValidateTemplate` currently *requires* `About` and `SuitableFor` non-null —
this is relaxed to require only `Id` + `Name` + `Sections`. When surfacing a custom template
to the trimmed API, `About` is mapped to `description` (falling back to empty). No DB column
rename, no migration; the write path (out of scope) will later write the new shape directly.

- **Rationale:** the proposal guarantees "custom-template rows are unaffected" and "no
  persisted-data migration." Tolerant read preserves existing rows while the API speaks the
  new vocabulary.

### D7. csproj content inclusion

Add `<Content Include="Issue/Services/IssueTemplates/templates/*.md" CopyToOutputDirectory="PreserveNewest" />`
alongside the existing `*.workflow.yaml` / `*.prompt` entries. The loader resolves the
directory relative to the assembly / `AppContext.BaseDirectory`, mirroring how workflow YAML
is located at runtime.

### D8. `DisableDefaultIssueTemplate` semantics

The existing project flag `ProjectWorkflowProfileRow.DisableDefaultIssueTemplate` gates the
built-in template(s). With three built-ins, it continues to gate **all built-ins** (project
sees only its custom templates when set). This preserves the flag's intent ("I want only my
own templates") and avoids inventing per-template enablement, which is out of scope.

- **Open question:** whether "disable default" should instead gate only the canonical
  `feature` and leave Bug/Refactor visible. See Open Questions.

## Risks / Trade-offs

- [Template files missing from build output at runtime] → Mitigation: `PreserveNewest`
  content include + a spec that asserts the three built-ins load end-to-end after build.
- [Breaking the `/api/issue-templates` response shape for an uncaptured consumer] →
  Mitigation: `TreatWarningsAsErrors` plus lockstep updates to Web (`types.ts`,
  `CreateIssueDialog.tsx`, client/queries tests) and CLI specs; the response-shape change
  is explicit and tested in `IssueTemplateApiSpecs`.
- [`mohist/default` alias silently drifting from `feature`] → Mitigation: a spec asserting
  both ids resolve to identical output.
- [Body parser fragility across the three asset files (comment/placeholder extraction)] →
  Mitigation: parser specs that read the actual `templates/*.md` and assert each section's
  title/guidance/placeholder; the README documents the file format as a contract.
- [Custom DB rows with the old `About` field surfacing a stale/empty `description`] →
  Mitigation: tolerant mapping (`About` → `description`); acceptable since the write path
  is a separate issue.
- [`PromptFrontmatterParser` type leak into the Issue feature] → Mitigation: accepted as a
  bounded trade-off; generalize only if a third consumer appears.

## Migration Plan

No persisted-data migration. Deployment is a server rebuild + restart (via `mo update
server` per project convention — not manual `dotnet run`, to avoid runner-id drift):

1. Build server — csproj copies `templates/*.md` to output; loader discovers the three
   built-ins.
2. Deploy server; `/api/issue-templates` now returns the trimmed shape with three entries.
3. Web and CLI ship in lockstep with the new response shape.

**Rollback:** revert the change in source (git revert). Because `MohistDefaultIssueTemplate`
is deleted by this change, rollback restores it; there is no partial/forward-only state
since no DB migration occurred. No data cleanup is required.

## Open Questions

- **`DisableDefaultIssueTemplate` granularity:** gate all three built-ins (D8, current
  leaning) or only the canonical `feature`? Needs a product call if projects rely on seeing
  Bug/Refactor while hiding the default.
- **Custom-template `description` source:** map legacy DB `About` → `description` (D6), or
  leave custom templates' `description` empty until the write path lands? Leaning toward
  mapping to avoid empty dropdown entries.
- **Generalize frontmatter parsing:** promote a shared frontmatter primitive out of
  `Workflow.Services.Prompts` now, or defer until a third consumer appears (D1)?
