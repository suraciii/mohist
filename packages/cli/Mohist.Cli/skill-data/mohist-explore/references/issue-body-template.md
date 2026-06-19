# PRD Body Template (three-voice)

Use this template when producing an issue body from `mohist-explore`. This is **pure content** — no frontmatter, no workflow id, no risk field. Those are added by the `mohist` skill at issue-creation time. This template captures only what the three voices produced.

User Voice, Product Shape, Acceptance Criteria, and Non-Goals are always present, in this order. **Domain Model is optional** — include it only when the requirement touches a non-trivial business domain (invariants, lifecycle rules, cross-aggregate constraints); omit it for pure technical corrections with no complex business scenario. When present, Domain Model sits between Product Shape and Acceptance Criteria. Each section is a level-2 (`##`) heading.

Write every section in product/domain language. The body must not cite source paths, file names, line numbers, or symbol names.

## Template

```markdown
## User Voice

<The user's own need, in the user's own words. The scenario where it matters, the
decision they cannot make, or the place they get stuck. Recorded faithfully, not
translated into product or technical terms. Minimum one sentence; expand as needed
to capture the real intent.>

## Product Shape

<The PM translation: the target product form — what the user will see or be able
to do, written in product language. State the boundary (what is in scope). Do NOT
cite source paths, file names, or symbol names — describe the product form. Make
trade-offs explicit.>

## Domain Model

<Optional. Include only when the requirement touches a non-trivial business domain
— invariants, lifecycle rules, or cross-aggregate constraints. Omit for pure
technical corrections with no complex business scenario. State the key concepts,
invariants, and constraints in the domain's own vocabulary. Do NOT cite source
paths, files, symbols, or line numbers. Do NOT prescribe implementation.>

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
alongside the existing title and body fields. The selector pre-fills from the
body's frontmatter `risk` value when one is present, and lets the user override
it before submitting. Out of scope: server-side risk validation rules beyond the
existing enum check, and how risk renders on the issue detail page.

## Domain Model

Risk is an optional attribute on an issue (`low | medium | high`). The invariant
is that risk, once set, feeds into workflow profile selection at plan time — so
the create dialog must commit a value the workflow engine can later read. The
create-issue endpoint already accepts risk in its request body; the gap is purely
in the Web UI binding. No data-model change needed.

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

- [ ] Sections appear in order: User Voice, Product Shape, [Domain Model], Acceptance Criteria, Non-Goals. Domain Model is present only when the requirement has a non-trivial business domain.
- [ ] User Voice is the user's need, not a solution. The user can recognize it as their own.
- [ ] Product Shape names a clear boundary and at least one real non-goal, written in product language.
- [ ] Domain Model (if present) stays in the problem space, in domain language — no prescribed files, functions, or task breakdown.
- [ ] The PRD body contains no source paths, file names, line numbers, or symbol names.
- [ ] The prose is literal and concise — no metaphors, no anthropomorphism, no fancy jargon; concepts use the product's own vocabulary.
- [ ] Acceptance Criteria are observable from the user perspective, not implementation checks.
- [ ] The PRD contains no frontmatter and no workflow/risk fields — `mohist` owns those.
