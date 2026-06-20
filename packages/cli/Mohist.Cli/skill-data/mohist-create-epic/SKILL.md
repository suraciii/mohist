---
name: mohist-create-epic
description: 创建 Mohist epic 的机械执行：写里程碑描述（Goal/Background/Non-goals/Scope）、定优先级、确认后跑 mo epic create，并 link 子 issue、设 prerequisite、管理 done/close 生命周期。当用户要把一组已探索好的、共享同一里程碑目标的 issue 作为 epic 落地时使用。触发词包括 "创建 epic"、"建 epic"、"link issue to epic"、"mo epic create"、"epic 生命周期"。issue 还是 epic 的决策由 mohist-explore 完成。
---

# mohist-create-epic

This skill owns the **mechanics** of creating a Mohist epic. An epic is an
organizational milestone that groups 3+ issues toward a single product goal; it
does **not** participate in workflow execution and has no risk or workflow
fields. Epic content is a lightweight milestone description, not the five-section
PRD used for issues.

Whether to create an epic (versus standalone issues) is decided upstream in
`mohist-explore` — its Scope stage determines whether the work shares one
milestone goal. This skill executes epic creation once that decision is made;
it does not re-litigate issue-vs-epic.

### Epic shape (no frontmatter)

Unlike issues, an epic has **no frontmatter, no workflow, no risk**. Do not invent
frontmatter fields for an epic — they are ignored. An epic has only:
`title`, `description` (long markdown), `priority`, and a derived `status`.

The `description` follows the milestone template in `references/epic-templates.md`:
Goal, Background, Non-goals, Scope (the issues it will contain).

### Priority guidance for epics

Epic priority rates the **milestone's** importance, not any single issue's. Use
`p0`–`p3` (lowercase), same scale semantics as issues but applied to the milestone.

### Creating an epic

```bash
mo epic create "<title>" --description "<markdown>" --priority p2
# -d / --description: the milestone markdown (see epic-templates.md)
# -p / --priority: p0|p1|p2|p3
# --project <id>: target project (else active project)
```

Note: `mo epic create` currently takes the description inline via `-d` only; there
is no `--description-file` yet. For long descriptions, write the markdown to a
file first, then pass its contents to `-d` via your shell, or use the API. (A
`--description-file` flag to match `mo issue create --body-file` is tracked as a
follow-up.)

### Linking issues to an epic

```bash
mo epic link <epic-id-or-number> <issue-id-or-number>
mo epic unlink <epic-id-or-number> <issue-id>
```

Constraint: **an issue belongs to at most one primary epic.** Linking an issue
already in another epic fails with `DUPLICATE_EPIC_MEMBERSHIP`. Both args accept
id or number.

### Setting issue prerequisites (execution order)

When an epic's issues have a start order (issue B requires issue A first), record
it as prerequisites so the epic can advance one issue at a time without false
starts:

```bash
# CLI does not yet have a prerequisite command — use the API:
curl -X POST http://localhost:3456/api/projects/<project>/issues/<B>/prerequisites \
  -H "Content-Type: application/json" \
  -d '{"prerequisiteNumber": <A-number>}'
```

A starts first; B becomes start-blocked ("waiting for #A") until A is delivered,
then B is free to start. Prefer fewer prerequisites — only real data/scaffold/invariant dependencies.

### Lifecycle: done vs close

- `mo epic done <id>` — marks the milestone shipped. Requires **all** linked
  issues delivered; else fails with `EPIC_NOT_READY_TO_MARK_DONE`.
- `mo epic close <id>` — abandons the milestone (not done, just dropped).

Use `done` for completed milestones, `close` for cancelled ones.

### User confirmation flow

Before creating, present to the user and wait for confirmation:

1. `title`, a one-line `description` gist, and `priority`.
2. The planned linked-issue list (numbers + titles) — or state "link later".
3. On confirm, run `mo epic create`; then `mo epic link` for each planned issue.

Never create an epic without confirmation.

### End-to-end creation checklist

- [ ] `description` follows Goal/Background/Non-goals/Scope.
- [ ] `priority` is `p0`–`p3`.
- [ ] No frontmatter/workflow/risk fields invented.
- [ ] User confirmed title, description summary, priority, and link plan.
- [ ] Lifecycle choice (done later vs close) is understood.
