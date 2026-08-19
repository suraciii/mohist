# Composite Issues and Sub-issues

Some requirements are too large for one Issue. A common case spans multiple
repositories, where each repository change must complete its own Workflow. A
composite Issue tracks the complete requirement in one Issue while delegating
execution to several child Issues.

## Mental Model

- After an Issue gains child Issues, it becomes a **parent Issue**, also called
  a composite Issue. The parent no longer runs its own Workflow. Delivery of
  the parent equals delivery of all its children.
- A **child Issue is a complete, normal Issue**. It has its own target
  repository, Workflow, approval points, and prerequisites. Its execution is
  identical to that of an independently created Issue.
- The hierarchy has only one level. A child Issue must not have child Issues.
- Composite Issues and Epics are independent. An Epic organizes and feeds work
  for a product goal. A composite Issue divides the internal work of one
  requirement. A child Issue does not belong to an Epic. An Epic treats the
  parent as a normal Issue.

Use an Epic when each part is independently valuable and each completion moves
the product forward. Use a composite Issue when the parts are only divisions
of one requirement and a partial result has no product value. For example, a
server API without its required Web integration is only part of one delivery.

## Split an Issue

Put the shared background, complete goal, and overall acceptance criteria in
the parent body. Give each child body a clear statement of its own scope.

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

Keep the parent and children as Drafts while assembling the split. This prevents
partial structure from entering execution before every scope and prerequisite
is visible. A Draft child remains intentionally excluded from composite
advancement.

- To attach an existing backlog Issue, use
  `mo issue edit 43 --parent 42`. To detach it, use
  `mo issue edit 43 --parent none`.
- A child Issue without an explicit priority inherits the parent's priority.
- You or an External Agent acting for you decide how to split the work. Mohist
  does not split it automatically.
- When a child enters Plan, Mohist gives the Inline Agent the parent title and
  body as background context. You do not need to copy shared context into each
  child body.

The following constraints apply:

- An Issue that has entered a Workflow must not be split or attached as a child.
  Stop it first.
- A terminal parent must not accept another child. Reopen it first.
- An Issue that has children must not become another Issue's child. The hierarchy
  remains one level deep.

## Advance a Composite Issue

```bash
mo issue start 42    # Starting the parent starts composite advancement.
```

After the parent starts:

1. Mohist starts all startable child Issues in parallel. Each child must be in
   `backlog`, marked ready, and have all prerequisites satisfied. Each runs its
   own Workflow in its own repository.
2. Whenever a child reaches a terminal state, Mohist automatically starts any
   children that the completion unblocked. This continues until all children
   are terminal.
3. The normal concurrency limit still applies. Composite advancement does not
   bypass it.

Composite advancement is parallel by design. This differs from the sequential
feeding of an Epic. An Epic controls work in progress for one goal. Child Issues
divide one delivery, so they should finish as early as possible. Prerequisites
express any required order.

You can also start each child manually with `mo issue start` without starting
the parent. Composite advancement is a convenience, not the only entry point.

Approval, retry, rerun, pause, and stop operations all apply to child Issues.
The parent has no Workflow and no approval points.

## State and Progress

The parent state is derived from the child Issues and does not need manual
maintenance. The parent is `backlog` when composite advancement has not
started and no child has started. It is `in-progress` when composite
advancement has started or any child has started. It becomes `done`
automatically when all children are terminal and at least one is `done`, and
`cancelled` automatically when all children are cancelled.

- A parent has no Workflow Stage. Its details show the child list, delivered
  progress such as X/Y Done, and the blocked count. Its board card shows a
  progress badge and problem indicator.
- When a child is blocked, run recovery actions such as retry, rerun, or resume
  on that child.

## Lifecycle Details

- **Close a parent**: All children must be terminal. Mohist rejects the action
  otherwise, so handle each child first. Close does not cascade implicitly.
- **Reopen a parent**: The parent returns to `backlog`. You can add more children
  and start it again.
- **Archive**: Archiving a parent also archives its children. A child must not be
  archived separately.
- **Detach a child**: The child becomes a normal Issue, and Mohist recalculates
  the parent immediately. After all children are detached, the parent becomes
  a normal Issue that can run its own Workflow.
- When a child is reopened, a completed parent automatically returns to
  `in-progress`.

## Relationship to Epics

- A child Issue must not join an Epic. Mohist rejects the link, and Epic automatic
  advancement never operates on a child.
- A parent can join an Epic. The Epic treats it as a normal Issue. Starting the
  parent triggers composite advancement, and parent completion counts toward
  Epic progress. The Epic does not inspect the composite structure, and this
  capability does not otherwise change Epic behavior.

## Relationship to Prerequisites

Prerequisite rules do not change. Common uses with a composite Issue are:

- **Between child Issues**: Express internal order, such as server before Web.
  Composite advancement honors it.
- **An external Issue depends on the parent**: Wait for the complete requirement
  to finish.
- **The parent depends on an external Issue**: Gate the start of composite
  advancement.

## End-to-End Acceptance

After each child Integrates, Mohist does not automatically run cross-repository
integration validation. When needed, create an integration-validation child as
the final child and make it depend on all other children. Coordinated release
of multi-repository changes is a Non-goal. See [Repositories](repositories.md).

## Status

Parent-child relationships, composite advancement, derived parent state, Epic
isolation, and read-only parent context during a child Plan are implemented.
Cross-repository integration remains an explicit
final-child workflow when a requirement needs it; Mohist does not infer or hide
that acceptance boundary. See
[`design/issue-breakdown.md`](../design/issue-breakdown.md) for the design.
