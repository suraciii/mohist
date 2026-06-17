# PRD Body Template (three-voice)

Use this template when producing an issue body from `mohist-explore`. This is **pure content** — no frontmatter, no workflow id, no risk field. Those are added by the `mohist` skill at issue-creation time. This template captures only what the three voices produced.

The sections must appear in this exact order. Each is a level-2 (`##`) heading.

## Template

```markdown
## User Voice

<The user's own need, in the user's own words. The scenario where it matters, the
decision they cannot make, or the place they get stuck. Recorded faithfully, not
translated into product or technical terms. Minimum one sentence; expand as needed
to capture the real intent.>

## Product Shape

<The PM translation: the target product form — what the user will see or be able
to do. State the boundary (what is in scope) and cite what you observed in the
current product (pages, commands, flows). Make trade-offs explicit.>

## Domain Model

<The domain expert translation: the key concepts, invariants, and constraints that
shape the solution — just enough for the Plan stage to understand the problem.
Cite the code paths and data models inspected. Do NOT prescribe implementation
(files, functions, tables, task steps).>

## Acceptance Criteria

- [ ] <Observable, verifiable condition, described from the user perspective>
- [ ] <Observable, verifiable condition>
- [ ] <Observable, verifiable condition>

## Non-Goals

- <Explicit out-of-scope item>
- <Explicit out-of-scope item>
```

## Worked example

A user wants the create-issue dialog to surface a risk selector:

```markdown
## User Voice

When I create an issue from the Web UI, I have no way to communicate how risky it
is. I end up pasting "this is high risk" into the body, and half the time the team
misses it. I want risk to be a first-class choice at creation time, not buried in
prose.

## Product Shape

Surface a `low / medium / high` risk selector in the create-issue dialog,
alongside the existing title and body fields. The selector should pre-fill from
the body's frontmatter `risk` field when one exists, and let the user override it
before submitting. Explored the dialog at `packages/web/src/issues/CreateIssueDialog.*`:
the `Issue` model already exposes `risk?: string | null`, but the dialog never
binds it. Out of scope: risk validation rules on the server (beyond the existing
enum check), and how risk renders on the issue detail page.

## Domain Model

`Issue.Risk` is an optional enum (`low | medium | high`) persisted on the issue
aggregate. The invariant is that risk, once set, flows into the workflow profile
selection at plan time — so the create dialog must commit a value the workflow
engine can read. The create-issue API endpoint already accepts `risk` in the
request body; the gap is purely in the Web UI binding. No schema change needed.

## Acceptance Criteria

- [ ] Create-issue dialog shows a risk selector with `low`, `medium`, `high` options.
- [ ] Selector pre-fills from the body's frontmatter `risk` field when present.
- [ ] User-chosen value overrides the frontmatter value on submit.
- [ ] The selected risk reaches the server in the create-issue request body.

## Non-Goals

- No server-side risk validation beyond the existing enum check.
- No change to how risk renders on the issue detail page.
- No automated risk suggestion based on labels or title.
```

## Verification before handoff

Before handing the PRD to the `mohist` skill to create the issue, confirm:

- [ ] The five `##` sections appear in order: User Voice, Product Shape, Domain Model, Acceptance Criteria, Non-Goals.
- [ ] User Voice is the user's need, not a solution. The user can recognize it as their own.
- [ ] Product Shape names a clear boundary and at least one real non-goal.
- [ ] Domain Model stays in the problem space — no prescribed files, functions, or task breakdown.
- [ ] Acceptance Criteria are observable from the user perspective, not implementation checks.
- [ ] The PRD contains no frontmatter and no workflow/risk fields — `mohist` owns those.
