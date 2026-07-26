# Built-in issue templates

Source files for the built-in issue templates. Each `.md` file is one template: frontmatter holds the metadata, the body is the raw template markdown.

## File format

```markdown
---
name: <template name>
description: <one-line description; the basis for AI/human template selection>
---

## <Section 1>

<!-- inline guidance: what to write / what NOT to write; optional sections state when to delete the whole section -->

<placeholder>
```

- **Frontmatter has only two fields**: `name`, `description`. The filename is the template id (`feature.md` -> `feature`).
- **`description` is the selection signal**: template selection is done by an AI or a human reading the description (not programmatic matching), so the three templates' descriptions must clearly distinguish each other — the core discriminator is "does external behavior change?".
- **Body = `## {title}` + `<!-- inline guidance -->` + `<placeholder>`**, per section. The guidance is a multi-line HTML comment carrying the per-section writing instructions (what to write / what NOT to write / when to delete an optional section).

## How the body is served

The server treats the body as an **opaque raw string**. It does not parse sections, does not extract guidance, does not strip the HTML comments:

- `GET /issue-templates` (list) returns metadata only — the body is never read.
- `GET /issue-templates/{id}` (detail) returns the full body verbatim, comments included.

This mirrors GitHub's classic markdown issue templates, which populate the issue body verbatim including `<!-- comments -->`. Consumers (the Web `CreateIssueDialog`, the `mo issue template view` CLI output, the `mohist-create-issue` skill) all receive the same raw body. Nothing strips the guidance comments — they are hidden in rendered markdown but visible in the raw text the AI planner reads, where they double as fill-time instructions.

## Two-tier, on-demand loading

Mirrors skill discovery (`SkillAssetService.TryReadFrontmatter`):

| Tier | Trigger | Reads | Output |
|---|---|---|---|
| Discovery | `mo issue template list` / AI selecting a template | **frontmatter only** | name + description (enough for AI/human to judge) |
| Detail | `mo issue template view <name>` / `composeIssueTemplateBody` | frontmatter + **full body** | the raw body string |

The discovery tier never reads the body; the body is loaded only after a template is selected.

## Adding a template

Drop a new `.md` file in this directory (the loader scans it). Frontmatter requires `name`/`description`; the body needs at least one `##` section. The id (filename) must not collide with an existing one.
