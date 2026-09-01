# OpenSpec Removal

Status: accepted

## Problem

OpenSpec was once the Workflow's implicit plan protocol: planning material was
written as OpenSpec changes inside the repository, runner actions consumed
`openspec/*` artifacts, and change archives accumulated under `openspec/`. The
planning protocol has since narrowed to the task list as the only
machine-readable plan artifact ([`../workflow/plan-artifacts.md`](../workflow/plan-artifacts.md)),
leaving the OpenSpec material as repository residue.

## Decision

OpenSpec carries no role in the product: no action, no built-in Workflow step,
no production symbol, and no in-repo content. Archived change material is
stored outside the repository at the parent workspace layer, not in git. This
record is the only OpenSpec reference in the project context; code-level
guards were removed with it. Git history before the removal commits holds the
original material.

## Alternatives considered

**Keep in-code anti-reintroduction guards.** The retired-action probe, the
comment-rule pattern, and definition assertions stayed green after removal,
but they spread OpenSpec names through live code and tests; a decision record
carries the history with no runtime residue.

**Archive the change material in the repository.** One place to read, but git
would keep carrying 2,300 historical files that no build or Workflow
consumes.
