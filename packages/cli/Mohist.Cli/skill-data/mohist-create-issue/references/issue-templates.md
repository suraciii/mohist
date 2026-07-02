# Mohist Issue Body Template (frontmatter + three-voice content)

Use this template when creating a Mohist issue. The file is the only contract between the PRD content (produced by `mohist-explore`) and the CLI: it MUST start with the frontmatter block, followed by the structured sections in the order shown. Domain Model is optional — include it only when the requirement has a non-trivial business domain; otherwise omit the whole section. Write the file to a temp path (for example `issue-body.md`) and hand it to `mo issue create <title> --body-file issue-body.md`.

## Frontmatter fields

| Field | Required | Values |
|---|---|---|
| `recommended_workflow` | yes | An enabled profile id returned by `mo workflow list --described`. The skill's selection rule (default profile → operator-chosen enabled id → first enabled profile as last resort) decides which id you write here. If discovery returns no enabled profile, stop and ask the user to enable a workflow first. |
| `recommended_workflow_reason` | yes | One short natural-language sentence explaining the choice — for example that you used the project's default, that you used the operator's explicit choice, or that no default was configured and you used the first enabled profile as a last resort. Use the YAML `\|` block scalar for multi-line reasons. |
| `risk` | yes | One of `low`, `medium`, `high`. Driver must be documented in `## Product Shape` or `## Acceptance Criteria`. |

## Template

```markdown
---
recommended_workflow: <enabled-profile-id-from-discovery>
recommended_workflow_reason: |
  Using the project's default workflow profile returned by mo workflow list
  --described.
risk: medium
---

## User Voice

<The user's own need, in the user's own words. The scenario where it matters and
where the current experience fails them. Recorded faithfully, not translated into
product or technical terms.>

## Product Shape

<The PM translation: the target product form — what the user will see or be able
to do, written in product language. State the boundary. Do NOT cite source paths,
file names, or symbol names. Make trade-offs explicit.>

## Domain Model

<Optional. Include only when the requirement touches a non-trivial business domain
— invariants, lifecycle rules, or cross-aggregate constraints. Omit for pure
technical corrections. State key concepts, invariants, and constraints in the
domain's own vocabulary. Do NOT cite source paths, files, symbols, or line
numbers. Do NOT prescribe implementation.>

## Acceptance Criteria

- [ ] <Observable, verifiable condition, from the user perspective>
- [ ] <Observable, verifiable condition>
- [ ] <Observable, verifiable condition>

## Non-Goals

- <Explicit out-of-scope item>
- <Explicit out-of-scope item>
```

## Worked example

A UI affordance problem: the create-issue dialog offers no risk selector.

```markdown
---
recommended_workflow: <enabled-profile-id-from-discovery>
recommended_workflow_reason: Using the project's default workflow profile as the operator has not specified a different one for this issue.
risk: low
---

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

## Verification before invoking `mo issue create`

- [ ] Frontmatter block is the first thing in the file, delimited by `---`.
- [ ] All three required fields are present and non-empty.
- [ ] `risk` is exactly one of `low`, `medium`, `high`.
- [ ] `recommended_workflow` is a real enabled id returned by `mo workflow list --described`; if no enabled profile is returned, do not create the issue until the user enables a workflow.
- [ ] `recommended_workflow_reason` is one short natural-language sentence explaining the choice (default, operator-chosen, or first-enabled fallback) — no tag citations or scoring rationale.
- [ ] Sections appear in order: User Voice, Product Shape, [Domain Model], Acceptance Criteria, Non-Goals. Domain Model is optional.
- [ ] The body contains no source paths, file names, line numbers, or symbol names.
- [ ] User has confirmed the workflow, risk, and a body summary before running `mo issue create --body-file`.
