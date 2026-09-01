# Composite Issues and Child Issues

A Composite Issue groups child Issues under one parent requirement. The parent
tracks the complete requirement. Each child keeps its own Workflow. Product
behavior is defined in [`../docs/composite-issues.md`](../docs/composite-issues.md)
and repository rules in [`../docs/repositories.md`](../docs/repositories.md).
The decision result is recorded in
[`decisions/composite-issues.md`](decisions/composite-issues.md).

## Design Drivers

- Parent-child is Issue organization, not a new aggregate type or a second
  workflow model.
- Parent state is derived from child state. No manually maintained aggregate
  status may become a second authority.
- A child keeps its own target Repository and execution scope.
- Composite advancement reuses ordinary start constraints and does not add a
  separate scheduler.

## Model

- A child Issue has one directional `parent` reference. The relation has one
  level. A child cannot own another child. Do not add generic `IssueLink`.
  `blocks` and `relates` remain rejected. Prerequisites express start order;
  `parent` expresses origin.
- A parent is an ordinary Issue that has at least one child. Removing all
  children makes it ordinary again.
- Project owns uniquely named Repositories and exactly one default. Issue
  stores a target Repository name and resolves an omitted target to the
  default. A parent target has no execution meaning because a parent does not
  enter Workflow.
- Parent state derives from children:
  - All children terminal with at least one Done makes the parent Done.
  - All children Cancelled makes the parent Cancelled.
  - Reopening any child moves a completed parent to In Progress.
  - Explicit parent start or any child start moves the parent to In Progress.

## Ownership and Invariants

- Issue owns the parent-child relation, derived state, and composite advancement.
- Workflow has no parent-child awareness. A child WorkflowRun behaves like an
  ordinary Issue run. A parent creates no WorkflowRun. The static
  `Issue -> Workflow` dependency remains one way.
- Dispatch carries no parent payload. A child Issue knows its parent through
  the `parent` reference, and its Agent reads the parent with the `mo` CLI
  when the work requires it. The child body remains the delivery-scope
  authority. See [`workflow/task-dispatch.md`](workflow/task-dispatch.md).
- Project Space owns the Repository collection and default resolution. Dispatch
  resolves the target Repository to its path and base branch.
- Parent and child are separate Issue aggregates. A child terminal or reopen
  event causes durable parent recomputation and sibling advancement. See
  [`workflow/issue-coordination.md`](workflow/issue-coordination.md).

## Composite Advancement

- Starting a parent starts every startable child concurrently. A startable child
  is Backlog with satisfied prerequisites.
- A child terminal transition reevaluates the composite and starts newly
  unlocked children until all are terminal.
- Ordinary concurrency and Runner-presence constraints still apply. If capacity
  is unavailable, wait for the next reevaluation. Do not add scheduler state.
- Manual start of one child is always allowed and does not conflict with
  composite advancement.

## Separation from Epic

Epic treats a parent as an ordinary Issue. Auto-advance may start it, which
triggers composite advancement. Existing progress recomputation observes parent
Done.

A child cannot link to an Epic. The Issue-side link entry point rejects that
combination. Epic advancement and decisions do not change.

## Lifecycle Constraints

- Attach through create or update `--parent` only when the child is unstarted
  Backlog, the parent is unstarted or In Progress, and the child is not already
  a parent.
- Detach with `--parent none` at any time. Recompute parent state immediately.
- Starting a parent requires at least one child. An Issue without children uses
  ordinary Workflow start.
- Close a parent only when all children are terminal. Closing does not cascade.
- Archive a parent with all its children. A child cannot archive independently.
- The default Repository cannot be deleted. A Repository bound to a nonterminal
  Issue cannot be deleted.

## Non-Goals

- One Issue checking out multiple Repositories for integration work is not
  supported. Use a final integration child Issue.
- Coordinated multi-repository release is not part of this design.

## Web UI Boundary

[`web-ui.md`](web-ui.md) owns exact parent-card placement by status, progress
indicators, and blocked presentation.

## Status

The parent-child model, derived parent state, composite advancement, Epic
isolation, and CLI/Web surfaces are implemented. The dispatch boundary still
assembles read-only parent context for child planning; the pull model above is
the target. Cross-repository acceptance uses an explicit final integration
child.
