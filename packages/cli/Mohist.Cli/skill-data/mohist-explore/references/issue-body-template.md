# Issue Body Template (frontmatter-annotated)

Use this template when producing an issue body from `mohist-explore` findings. The file MUST start with the frontmatter block; the four structured sections follow in the exact order shown. Write the file to a temp path (for example `issue-body.md`) and hand it to `mo issue create <title> --body-file issue-body.md`.

## Frontmatter fields

| Field | Required | Values |
|---|---|---|
| `recommended_workflow` | yes | A profile id returned by `mo workflow list --described`, or `mohist/default` when nothing matches. |
| `recommended_workflow_reason` | yes | One sentence that names the matched `suitable_for` tag(s) or explains the fallback. Use the YAML `\|` block scalar for multi-line reasons. |
| `risk` | yes | One of `low`, `medium`, `high`. Driver must be documented in `## Background` or `## Acceptance criteria`. |

## Template

```markdown
---
recommended_workflow: mohist/default
recommended_workflow_reason: |
  No specific workflow matched the exploration findings; falling back to
  mohist/default.
risk: medium
---

## Background

<Distilled context from the exploration. State what was observed, where, and
why it matters to users. Cite the commands run, flows traced, and files
inspected so a reviewer can reproduce the finding.>

## Goal

<The single user-visible outcome this issue will deliver. One paragraph, no
implementation detail.>

## Non-goals

- <Explicit out-of-scope item>
- <Explicit out-of-scope item>

## Acceptance criteria

- [ ] <Observable, verifiable condition>
- [ ] <Observable, verifiable condition>
- [ ] <Observable, verifiable condition>
```

## Worked example

A UI affordance problem surfaced while exploring the create-issue dialog:

```markdown
---
recommended_workflow: mohist/default
recommended_workflow_reason: Findings touch UI affordances and a missing risk selector; mohist/default covers UI work.
risk: low
---

## Background

Explored the Web UI create-issue dialog on `packages/web/src/issues/`. The dialog
currently offers no risk selector even though the `Issue` model exposes
`risk?: string | null`. Users must manually edit the body to communicate risk,
which is consistently missed (3 of the last 5 issues created without a risk
value).

## Goal

Surface a `low/medium/high` risk selector in the create-issue dialog,
pre-filled from frontmatter when the body contains one.

## Non-goals

- Do not add server-side risk validation beyond the existing enum check.
- Do not change how risk renders on the issue detail page.

## Acceptance criteria

- [ ] Create-issue dialog shows a risk selector with `low`, `medium`, `high` options.
- [ ] Selector is pre-filled from the body's frontmatter `risk` field when present.
- [ ] User-chosen value overrides the frontmatter value on submit.
- [ ] `createIssue()` API client sends the selected risk in the request body.
```

## Verification before invoking `mo issue create`

- [ ] Frontmatter block is the first thing in the file, delimited by `---`.
- [ ] All three required fields are present and non-empty.
- [ ] `risk` is exactly one of `low`, `medium`, `high`.
- [ ] `recommended_workflow` is a real id (or `mohist/default`).
- [ ] The four `##` sections appear in the order: Background, Goal, Non-goals, Acceptance criteria.
- [ ] User has confirmed the workflow, risk, and a body summary before running `mo issue create --body-file`.
