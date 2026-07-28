# Mohist Epic Description Template

Epics are lightweight milestones — no frontmatter, no workflow, no risk. The
`description` is the only content artifact, handed to `mo epic create --description` (or the
API). Structure it as below. Contrast with `issue-templates.md`, which carries
frontmatter and a five-section PRD.

## Template

```markdown
# <Epic title>

## Goal
<One sentence: the milestone outcome — what is true when this epic is done.>

## Background
<Why this milestone, now. The current product gap or opportunity. Cite what you
observed (pages, flows, data).>

## Non-goals
- <Explicit out-of-scope item>
- <Explicit out-of-scope item>

## Scope
<The issues this epic will contain, as a bullet list of deliverables. Each bullet
becomes one issue; coarse is fine at epic-creation time. Aim for 3+ — if fewer,
it may not need to be an epic.>
- <deliverable 1>
- <deliverable 2>
- <deliverable 3>
```

## Worked example

```markdown
# Dashboard — Mohist's default landing

## Goal
Replace the Kanban-as-home with a glanceable dashboard so a returning user knows
in 5s whether to intervene and sees their productivity.

## Background
Current Home is an operational Kanban; it gives a state slice, not "what happened
while I was away" or any sense of accomplishment. Mohist's core moment is
"come back to harvest" — the default page should serve that moment.

## Non-goals
- No Grafana-style analytics.
- No Kanban operational features (those stay on the Issues page).

## Scope
- App-shell: dashboard as default landing + composition scaffold
- Issue context: attention derivation promoted to shared logic
- Issue context: completion productivity metrics
- Agent/Session context: usage aggregation
- Dashboard views: attention / pulse / productivity / digest
```

## Verification before invoking `mo epic create`

- [ ] Goal is one sentence naming the milestone outcome.
- [ ] Background cites observed product reality.
- [ ] Non-goals are brave (actually cutting things).
- [ ] Scope lists 3+ deliverables (else it may not need to be an epic).
- [ ] No frontmatter/workflow/risk fields present.
