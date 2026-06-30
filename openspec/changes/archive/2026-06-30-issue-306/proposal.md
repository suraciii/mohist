## Why

Mohist ships only one built-in issue template (the three-part Feature PRD), so Bug and Refactor issues are forced to masquerade as Feature and fabricate a User Voice. That single template is hardcoded inside a C# class (`MohistDefaultIssueTemplate.cs`), and loading is not layered — `List()` deserializes every template's full body just to render a directory. Template metadata also carries fields (`suitableFor`, `defaults`, `isDefault`) that never drive selection, because choosing a template is a judgment made by an agent or human reading the `description`, not a programmatic match. We need the three templates that already exist as `templates/*.md` assets actually wired in, loaded in two stages (frontmatter on list, body on get), with metadata trimmed to what selection truly needs.

## What Changes

- Wire up a **file-asset loader** for the three built-in templates (`feature`/`bug`/`refactor`) that already exist at `Issue/Services/IssueTemplates/templates/*.md`, mirroring the skill discovery mechanism: discovery reads **frontmatter only**, detail reads **frontmatter + body**, reusing `PromptFrontmatterParser`.
- Refactor `IssueTemplateRegistry`: built-ins come from the file loader; **delete `MohistDefaultIssueTemplate.cs`**; `List()` returns metadata only, `Get()` loads the body.
- Remove the template path through `SuitableForMatcher.Matches()` — template selection is an agent/human judgment over `description`, not a programmatic match. (The workflow-profile `Matches()` path is unchanged.)
- Collapse template metadata to **`name` + `description`** only. **BREAKING** for the `/api/issue-templates` response shape and the `IssueTemplateInfo` / `IssueTemplateDetail` records: `suitableFor`, `defaults`, and `isDefault` leave the template model, and `about` becomes `description`. Consumers (Web, CLI) are updated in lockstep.
- Supersede `mohist/default` with `feature`. Existing references keep working via an alias/compat shim, so this is non-breaking for callers.
- Out of scope: the `mohist-create-issue` skill rewrite (manual, non-code); Web dropdown surfacing of `description` (deferred); custom-template write path (separate issue).

## Capabilities

### New Capabilities
- `issue-templates`: The issue-template registry and loading model — three built-in templates (Feature/Bug/Refactor) as file assets, two-stage on-demand loading (frontmatter-only discovery, body-on-detail), metadata trimmed to `name` + `description`, selection by agent/human judgment over `description` (no programmatic matching), the `list`/`get` HTTP endpoints and CLI commands, and the `mohist/default` → `feature` supersession.

### Modified Capabilities
- _(none — issue-template behavior is not currently described by any existing spec.)_

## Impact

- **Server** (`packages/server`): new file loader + csproj `<Content Include=".../templates/*.md" CopyToOutputDirectory="PreserveNewest" />`; `IssueTemplateRegistry` reworked (built-ins from files, layered `List()`/`Get()`, drop `Matches()`); `IIssueTemplate` / `IssueTemplateInfo` / `IssueTemplateDetail` (`Api/IssueTemplateRoutes.cs`) field-trimmed; `MohistDefaultIssueTemplate.cs` and `IssueTemplates.DefaultId = "mohist/default"` replaced by a `feature` alias; `SuitableForMatcher` no longer invoked on the template path (the class itself stays for workflow profiles). TreatWarningsAsErrors enforces the build.
- **CLI** (`packages/cli`): `TableRenderer.IssueTemplates.cs` list/get rendering updated to the new `description`-only metadata; command help text referencing `mohist/default` updated.
- **Web** (`packages/web`): any consumer of the issue-template list/detail response shape aligned to the trimmed metadata (no template-selection dropdown change in scope).
- **Tests**: `IssueTemplateRegistrySpecs` (file loading, frontmatter-only list, body-on get, alias), `CliIssueTemplateCommandSpecs` (list shows name+description, get returns sections, `feature` resolves and `mohist/default` still resolves).
- **No persisted-data migration**: built-ins are file assets, not DB rows; custom-template rows are unaffected.
