# Built-in issue templates

Source files for the built-in issue templates. Each `.md` file is one template: frontmatter holds the metadata, the body holds the template (sections + inline guidance comments + placeholders).

## File format

```markdown
---
name: <template name>
description: <one-line description; the basis for AI/human template selection>
---

## <Section 1>

<!-- inline guidance: what to write / what not to write; optional sections state when to delete the whole section -->

<placeholder>
```

- **Frontmatter has only two fields**: `name`, `description`. The filename is the template id (`feature.md` -> `feature`).
- **`description` is the selection signal**: template selection is done by an AI or a human reading the description (not programmatic matching), so the three templates' descriptions must clearly distinguish each other — the core discriminator is "does external behavior change?".
- **Body structure**: `## {title}` + `<!-- inline guidance -->` + `<placeholder>`. The inline comment is carried into the generated issue body by `composeIssueTemplateBody`.

## Two-tier, on-demand loading

Mirrors skill discovery (`SkillAssetService.TryReadFrontmatter`):

| Tier | Trigger | Parses | Output |
|---|---|---|---|
| Discovery | `mo issue template list` / AI selecting a template | **frontmatter only** | name + description (enough for AI/human to judge) |
| Detail | `mo issue template get <name>` / `composeIssueTemplateBody` | frontmatter + **full body** | complete sections |

The discovery tier never parses the body; the body is loaded only after a template is selected.

## Adding a template

Drop a new `.md` file in this directory (the loader scans it). Frontmatter requires `name`/`description`; the body needs at least one `##` section. The id (filename) must not collide with an existing one.
