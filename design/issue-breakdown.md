---
status: accepted 2026-07-15
---

# Composite Issues and Child Issues

Product specs: [`docs/sub-issues.md`](../docs/sub-issues.md) and
[`docs/repositories.md`](../docs/repositories.md). This document records the
domain design, rationale, and constraints.

## Decision History

- The original #96 proposed automatic three-stage breakdown: Agent analysis,
  an `issue-breakdown.json` artifact, Approval, and bulk child-Issue creation.
  It was rejected. #281 lists automatic breakdown and bulk generation as
  Non-goals. That decision remains: decomposition is always an explicit choice
  by the owner or an External Agent acting for the owner.
- The earlier conclusion that child Issues overlapped with Epic assumed one
  Repository per Project. On 2026-07-15, multi-Repository resources introduced
  a real need to execute one requirement across Repositories. The decomposition
  axis, internal parts of one unit of work, is independent of the Epic axis,
  product-goal organization and work supply.
  - Epic members deliver independent value and are supplied serially to control
    work in progress.
  - Child Issues divide one unit of work. A partial result has no product value,
    and startable children advance concurrently.

## Model

- A child Issue holds one directional `parent` reference. This is the only
  relationship and has one level; a child cannot own another child. Do not add
  a generic `IssueLink`. `blocks` and `relates` remain rejected. Prerequisites
  express start ordering, and parent expresses origin.
- A parent is not a new Issue type. An Issue is composite when it has at least
  one child. Removing every child makes it an ordinary Issue again.
- Project owns a Repository collection with unique names, base branches, and
  exactly one default. Issue stores a target Repository name and resolves an
  omitted value to the default. A parent's target has no execution meaning
  because a parent does not enter a Workflow.
- Child state changes derive parent state; parent state is not maintained
  manually.
  - When all children are terminal and at least one is Done, the parent becomes
    Done. When all are Cancelled, the parent becomes Cancelled.
  - Reopening any child moves a completed parent to In Progress.
  - Explicit start or the start of any child moves the parent to In Progress.

## Ownership and Invariants

- Parent-child relation, state aggregation, and composite advancement belong to
  the **Issue subdomain** as work organization.
- **Workflow has no awareness.** A child WorkflowRun behaves like an ordinary
  Issue run. A parent creates no WorkflowRun. The static `Issue -> Workflow`
  dependency remains one-way; Workflow does not know Issue.
- At the HTTP dispatch-response boundary, a child Plan Inline Agent receives
  the current parent title and body as assembled background. They do not enter
  WorkflowRun, Workflow WorkDispatch, or task input. Runner's
  `mohist/opencode` Action marks this background read-only, while the child body
  remains authoritative for delivery scope. See
  [`workflow/task-dispatch.md`](workflow/task-dispatch.md).
- Repository collection and default resolution belong to **Project Space**.
  Dispatch resolves the target to Repository path and base branch instead of
  reading one Project Repository.
- Parent and child are different Issue aggregates. Domain events coordinate
  them: a child terminal or reopen event causes parent recomputation and sibling
  advancement. This follows the `WorkflowRunCompleted -> CompleteIssue`
  pattern. See
  [`workflow/issue-coordination.md`](workflow/issue-coordination.md).

## Composite Advancement

- Starting a parent starts every startable child, meaning Backlog with satisfied
  prerequisites, concurrently. A child terminal transition reevaluates and
  starts newly unlocked children until all are terminal.
- Reuse existing start constraints such as concurrency and Runner presence.
  When no capacity is available, wait for the next reevaluation. Do not add
  separate scheduler state.
- Manual start of one child is always allowed and does not conflict with
  composite advancement.

## Separation from Epic

- **Epic behavior does not change.** Epic treats a parent as an ordinary Issue.
  Auto-advance starts it, which triggers composite advancement. Existing
  progress recomputation observes parent Done.
- A child Issue cannot link to an Epic. Validate this at the Issue-side link
  entry point without changing Epic advancement or decisions.

## Lifecycle Constraints

| Operation | Constraint |
|---|---|
| Attach through create or update `--parent` | Child must be unstarted Backlog; parent must be unstarted or In Progress; an Issue that already has children cannot become a child |
| Detach with `--parent none` | Allowed at any time; recompute parent state immediately |
| Start parent | Requires at least one child; an Issue without children uses ordinary Workflow start |
| Close parent | Allowed only when all children are terminal; does not cascade |
| Archive parent | Children archive with the parent; a child cannot archive independently |
| Delete Repository | The default cannot be deleted; a Repository bound to a nonterminal Issue cannot be deleted |

## Open Questions

1. **Multiple checkouts:** One Issue checking out several Repositories for
   integration work is explicitly unsupported. The product spec uses a final
   integration child Issue. Reevaluate only after a real requirement appears.
2. **Web UI:** [`web-ui.md`](web-ui.md) owns exact parent-card placement by
   status without stage, progress indicators, and blocked presentation.
