# Composite Issues and Child Issues

A Composite Issue divides one requirement into child Issues when one delivery
needs coordinated changes. Each child remains a complete Issue with its own
Repository and Workflow. The parent tracks the complete requirement.

## Product Commitments

- A parent Issue tracks one complete requirement. Its child Issues divide the work.
- A child Issue is a normal Issue with its own Repository, Workflow, prerequisites, and Approval Points.
- The hierarchy has one level. A child Issue cannot have children.
- Parent state derives from child state and needs no manual maintenance.
- Composite advancement starts every startable child in parallel and respects normal concurrency limits.
- Parent completion requires terminal children. At least one `done` child produces `done`; all-cancelled children produce `cancelled`.
- An Epic may contain a parent Issue but never a child Issue.
- Detaching every child returns the parent to a normal Issue that can run its own Workflow.

## Mental Model

When an Issue gains child Issues, it becomes a parent Issue, also called a
Composite Issue. The parent no longer runs its own Workflow. Delivery of the
parent represents delivery of all its children.

A child Issue has its own target Repository, Workflow, Approval Points, and
prerequisites. Its execution is the same as an independently created Issue.
The hierarchy has one level only, so a child cannot have children.

Composite Issues and Epics organize work in different ways. An Epic groups
independently valuable deliverables under a product goal. A Composite Issue
divides one requirement when a partial result has no product value. A child
Issue never belongs to an Epic; an Epic treats the parent as a normal member.

## Split an Issue

Use a Composite Issue when parts are only divisions of one delivery, such as a
server API and its required Web integration. Put shared context in the parent
body and give each child a clear scope.

```bash
# Describe the complete requirement in the parent Issue.
mo issue create "Subscription notifications" --body-file ./subscription-feature.md

# Create child Issues. Assume the parent number is 42.
mo issue create "server: subscription API and event delivery" --parent 42 --repo server
mo issue create "web: subscription management page"             --parent 42 --repo web

# Add a prerequisite when order matters. The Web Issue waits for the server Issue.
mo issue prereq add 44 43

# The graph is complete. Mark the parent and every child ready before advancing it.
mo issue edit 42 --ready
mo issue edit 43 --ready
mo issue edit 44 --ready
```

Keep the parent and children as Drafts while assembling the split. This keeps
partial structure out of execution until every scope and prerequisite is
visible. A Draft child remains excluded from composite advancement.

- Attach an existing backlog Issue with `mo issue edit 43 --parent 42`.
  Detach it with `mo issue edit 43 --parent none`.
- A child without an explicit priority inherits the parent's priority.
- You or an External Agent decide the split. Mohist does not split Issues automatically.
- A child knows its parent through the `parent` reference. When the work needs
  shared context, its Mohist Agent reads the parent with `mo issue view`. You
  do not need to copy shared context into each child.

An Issue that has entered a Workflow must not be split or attached as a child;
stop it first. A terminal parent must be reopened before it accepts another
child. An Issue with children cannot become another Issue's child.

## Advance a Composite Issue

```bash
mo issue start 42    # Starting the parent starts composite advancement.
```

After the parent starts:

1. Mohist starts all startable child Issues in parallel. Each child must be in
   `backlog`, be ready, and have satisfied prerequisites. Each runs its own
   Workflow in its own Repository.
2. When a child reaches a terminal state, Mohist starts children that the
   completion unblocked. This continues until all children are terminal.
3. The normal concurrency limit still applies. Composite advancement does not
   bypass it.

You can start each child manually with `mo issue start` without starting the
parent. Composite advancement is a convenience, not the only entry point.

Approval, retry, rerun, pause, and stop operations apply to child Issues. The
parent has no Workflow and no Approval Points.

## State and Progress

The parent state derives from its children:

- `backlog`: composite advancement has not started and no child has started.
- `in-progress`: composite advancement or any child has started.
- `done`: all children are terminal and at least one child is `done`.
- `cancelled`: all children are `cancelled`.

A parent has no Workflow Stage. Its details show the child list, delivered
progress such as X/Y Done, and blocked count. Its board card shows a progress
badge and problem indicator. Run recovery actions such as retry, rerun, or
resume on a blocked child.

## Lifecycle Details

- **Close a parent**: All children must be terminal. Mohist rejects the action
  otherwise. Close does not cascade implicitly.
- **Reopen a parent**: The parent returns to `backlog`. You can add children and
  start it again.
- **Archive**: Archiving a parent also archives its children. A child cannot be
  archived separately.
- **Detach a child**: The child becomes a normal Issue, and Mohist recalculates
  the parent immediately. After all children are detached, the parent becomes
  a normal Issue that can run its own Workflow.
- **Reopen a child**: A completed parent returns automatically to `in-progress`.

## Relationship to Epics

- A child Issue cannot join an Epic. Mohist rejects the link, and Epic
  advancement never operates on a child.
- A parent can join an Epic. The Epic treats it as a normal Issue. Starting the
  parent triggers composite advancement, and parent completion counts toward
  Epic progress.
- The Epic does not inspect the Composite Issue structure, and these rules do
  not otherwise change Epic behavior.

## Relationship to Prerequisites

Prerequisite rules do not change. Common uses with a Composite Issue are:

- **Between child Issues**: Express internal order, such as server before Web.
  Composite advancement honors it.
- **An external Issue depends on the parent**: Wait for the complete requirement.
- **The parent depends on an external Issue**: Gate composite advancement.

## End-to-End Acceptance

After each child Integrates, Mohist does not automatically run cross-Repository
integration validation. When needed, create an integration-validation child as
the final child and make it depend on all other children. Coordinated release of
multiple Repositories is not automatic. See [Repositories](repositories.md).

## Implementation Gaps

Parent-child relationships, composite advancement, derived parent state, and
Epic isolation are implemented. The dispatch boundary still assembles the
parent title and body as read-only background for a child Plan. The target is
pointer-only context: the Agent reads the parent through the CLI when needed.
Cross-Repository integration remains an explicit final-child Workflow when a
requirement needs it. Mohist does not infer or hide that acceptance boundary.
See [`design/composite-issues.md`](../design/composite-issues.md) for the design.
