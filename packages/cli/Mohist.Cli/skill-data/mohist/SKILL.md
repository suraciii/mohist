---
name: mohist
description: Use for Mohist issues, epics, WorkflowRuns, projects, and related operations. This Skill is the decision entry point: establish current state, choose the scenario Skill when one exists, make Mohist-specific state decisions, then hand exact syntax to the current `mo` help.
---

# Mohist

## Scope

This Skill dispatches work involving Mohist issues, epics, WorkflowRuns, projects,
and their supporting resources. It helps choose the next decision; it is not a
second command reference and it does not replace a scenario Skill.

## First read

Before interpreting or changing existing work, read the current state with the
canonical CLI:

```bash
mo issue view <number>
mo run view <run-id>
```

Use the Issue read for an Issue context and the Run read for a WorkflowRun
context. Do not infer state from an old message, cached output, or a local
working tree. For a Run associated with an Issue, use the Run command's current
targeting help before choosing an Issue-based lookup.

## Scenario routing

Load the narrowest sibling Skill for the work:

- Exploring or distilling a requirement, including deciding Issue versus epic:
  `mohist-explore`.
- Creating an Issue from an agreed requirement: `mohist-create-issue`.
- Creating an epic, linking its Issues, setting prerequisites, or driving its
  milestone lifecycle: `mohist-create-epic`.

The sibling Skill owns its detailed questions, confirmation points, and command
sequence. Do not reproduce those procedures here. For routine reads, workflow
actions, or resources without a sibling Skill, continue to the current leaf
help.

## Mohist-specific decisions

These distinctions are domain decisions; exact arguments and flags belong to
leaf help.

- `retry` retries the current failed point of a WorkflowRun. `rerun` starts the
  workflow again; `rerun --from-stage` invalidates the named stage and following
  work before starting again. Choose `retry` for the current failure and
  `rerun` when earlier work must be repeated.
- `pause` leaves a WorkflowRun resumable. `stop` is terminal and abandons the
  Run; use it only when that is intended. A stopped Run is not recovered by
  `resume`.
- `compact` changes a Session in place while retaining the Session. `reset`
  changes it in place by resetting its conversation state. Choose based on
  whether the Session should be condensed or reset; inspect the Session leaf
  help before acting.

For every state-changing action, read the relevant leaf help first:
`mo <command> --help`. Treat the command's current help as the authority for
arguments, flags, JSON fields, confirmation, and targeting.

## CLI handoff

Use the current command tree for all exact syntax. Start at `mo --help`, narrow
to a command group, and then read the leaf help before constructing an
invocation. Use `mo skill view <name>` when a sibling Skill needs to be loaded.

Help is local and safe to query. Do not assume a Project, service, or remote
request is needed just to discover syntax. When help does not answer a product
decision, return to the First read or the appropriate sibling Skill instead of
copying command tables into this entry point.

Use `--json <fields>` to select only the fields needed for a decision; for any
command that returns a resource, bare `--json` lists the available fields locally
without contacting the Server.
