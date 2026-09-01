# Aggregate Coordination

Issue and Epic belong to the Issue context. WorkflowRun belongs to the Workflow context. AgentJob, Runner, and
Session belong to Agent execution. This document defines their cross-context coordination without creating a
second owner for any business fact.

In the diagrams, `->` is a synchronous command and `[Event]` is an asynchronous reaction started by a
durable handler after commit. Each solid command enters exactly one target aggregate transaction. A command
caller and target do not share a transaction.

## System Boundary

Cross-aggregate workflows use a synchronous command to commit one fact and a durable event to close the
business loop. A query may be stale, but the target aggregate revalidates its current state before committing.
A synchronous call stack must never cycle back into an aggregate that is already calling.

## Write Authorities

Each business fact has one sole write authority:

- Issue.`EpicNumber?` says which Epic contains an Issue. Epic queries Issue, and WorkflowRun stores only minimal
  run context.
- Epic owns its lifecycle and advancement policy. Issue stores only `EpicNumber?` and does not copy Epic state.
- Issue owns its lifecycle and current WorkflowRun. Epic queries it, and WorkflowRun results return through
  events.
- WorkflowRun owns Workflow execution state. Issue stores only `WorkflowRunId`.
- Runner owns presence and capacity. Workflow scheduling consumes only its public facts.
- Session owns Session lifecycle. WorkflowRun and Agent store only associated identities.

There is no independent membership aggregate, generic `OwnerRef`, or controller aggregate. Member lists,
progress, and the next candidate Issue are queries over current Issue state. Epic cannot modify those facts
independently.

## Association and Migration

```text diagram
                                                    +-------+
                                                +-->| Epics |
+------+    +------+    +-------+    +---------+|   +-------+
| User +--->| Link +--->| Issue +--->| Changed ++
+------+    +------+    +-------+    +---------+|   +-----+
                                                +-->| Run |
                                                    +-----+
```

`LinkIssue` reads the Issue's current affiliation. If the Issue already belongs to this Epic, it returns success
even when the Epic became `closed` after the original request committed. A retry cannot turn that success
into failure. Only an unassociated Issue causes `LinkIssue` to check `closed` and send a write command.

`Issue.AssignEpic(epicNumber)` changes `EpicNumber?` from the old value to the new value in one Issue transaction. Moving an Issue
needs no unlink-then-link sequence and creates no state where two Epics own it. Assigning the same number is a
no-op. `Issue.RemoveEpic(expectedEpicNumber)` cannot clear a newer affiliation after a late command from the old Epic.

Epic validation and the Issue affiliation commit are separate transactions. If the Issue commits but the
response is lost, retrying `LinkIssue` returns the same idempotent result. If a later Epic save fails, `IssueEpicChanged`
still causes Epic to recompute. A `done` Epic reopens when an open Issue joins. An old Epic updates
progress when a member leaves.

A handler rereads current Issue state before sending a complete command to Epic or WorkflowRun. The active run
update uses `WorkflowRun.UpdateIssueContext(current Issue context)`. Out-of-order and duplicate events cannot write an old Epic number back.

## Epic Advances Issues

```text diagram
+------+    +-------+    +------+    +---------+    +-------+    +-----+
| User +--->| Start +--->| Epic +--->| Advance +--->| Issue +--->| Run |
+------+    +-------+    +------+    +---------+    +-------+    +-----+
```

After `Epic.Start` commits `EpicStarted`, a durable handler invokes `Epic.Advance`. The candidate query performed by Epic
may be stale. `Issue.TryStartFromEpic(epicNumber)` rechecks current `EpicNumber`, state, dependencies, and existing WorkflowRun inside
Issue. It rejects or no-ops when the candidate is no longer valid. Epic selects again later. Correctness does
not depend on atomicity between query and command.

Issue allocates and stores `WorkflowRunId` in its start transaction without writing WorkflowRun. The durable
`IssueWorkStarted` handler rereads Issue and calls:

`WorkflowRun.EnsureStarted(workflowRunId, ProjectId + IssueNumber + EpicNumber?)` only when the event still names the current active run. WorkflowRun creation is idempotent by
`WorkflowRunId` and enters its normal lifecycle directly. It needs no `AwaitingBinding`, `WorkflowBindingPending`, or lineage revision.
Event redelivery recovers failures before creation, after creation but before the response, or before handler
acknowledgement.

## Workflow Results and Continued Advancement

```text diagram
+--------+    +-----+       +-------+
| Runner +--->| Run +------>| Event ++
+--------+    +-----+       +-------+|   +-------+    +---------------+
                                     +-->| Issue +--->| Parent / Epic |
+------+      +--------+    +------+     +-------+    +---------------+
| User +----->| Manual +--->| Done |                          ^
+------+      +--------+    +---+--+                          |
                                |                             |
                                +-----------------------------+
```

WorkflowRun commits either `WorkflowRunCompleted` or `WorkflowRunFailed`. Durable handlers call `Issue.Complete(expectedWorkflowRunId)` or `Issue.AbortWork(expectedWorkflowRunId)`. Issue uses
`expectedWorkflowRunId` to reject a late result from an old run. The next Epic advance starts only from the terminal event
committed by Issue. WorkflowRun never modifies Epic directly.

Manual completion is an explicit Issue lifecycle command. It does not fabricate `WorkflowRunCompleted` or modify
WorkflowRun. Before commit, IssueGrain reads the currently bound run. Only `Stopped` and `Completed` are
accepted because they cannot be scheduled again. A `Failed` run can still be retried, so the user must stop
it first. The terminal read cannot race with resume or retry. Issue then rechecks that it remains `InProgress`
and bound to the same run before writing `IssueCompleted`.

The event's `completionKind` distinguishes `workflow` from `manual`. Parent Issues, Epic, Inbox, and metrics consume
the same completion event. A duplicate command against `Done` is a no-op, so a lost response cannot create
a second completion event. A parent Issue with children cannot be completed manually. Its terminal state comes
from a fresh child snapshot.

## Synchronous Direction and Asynchronous Closure

Aggregates may depend on each other in both directions, but each call stack has one direction:

- Association is Epic -> Issue. Issue does not call Epic synchronously from that command.
- Advancement is Epic -> Issue. Issue starts Workflow and notifies Epic through events.
- Execution result is WorkflowRun -> event -> Issue command. WorkflowRun does not call Issue synchronously.
- Affiliation refresh is Issue -> event -> Epic and WorkflowRun. Neither target calls Issue back from the
  command.

These paths form a business loop, but each commit contains exactly one aggregate.

## Session Ends Bound Workflow Work

```text diagram
+---------+    +---------+    +-----+
| Session +--->| Abandon +--->| Run |
+---------+    +---------+    +-----+
```

A Workflow-origin Session binds one WorkflowRun work item by `(runnerId, workId)`. When its active Turn settles with a
non-success terminal outcome, Session synchronously calls `WorkflowRun.AbandonActiveWork(runnerId, workId, reason)`. The settlement reason is either an
intended `Cancelled` stop or a Runtime-reported failure.

The command carries the frozen identities recorded at settlement and enters one WorkflowRun transaction. A
late or replayed command cannot abandon later work. WorkflowRun never calls Session back synchronously.
Replaying a settlement operation reissues the same idempotent abandon for `(runnerId, workId)`.

## Session and AgentJob Propagate One Way per Call Stack

```text diagram
+---------+    +-------------+    +----------+    +---------------+
| Session +--->| Job unknown +--->| Job fact +--->| Session async |
+---------+    +-------------+    +----------+    +---------------+
```

When Session stop recovery cannot confirm the stop of a launch Turn, Session synchronously marks the owning
AgentJob unknown. AgentJob never calls Session back synchronously from that command. Its initial-Turn
propagation reaches Session through a durable job-state fact and the existing asynchronous channel, such as an
event handler or Session recovery pass. The fact is replayed under the same identity until acknowledged.

No call stack holds one aggregate while calling it back. Any propagation that would form a synchronous cycle
uses the asynchronous leg.

## Other Interactions

Two additional one-way interactions exist:

- Issue -> Cancel -> WorkflowRun.
- Runner -> `[RunnerDisconnected]` -> Session, which fails affected Sessions.

## Non-Goals

- Coordination does not create an independent membership, owner, or controller aggregate.
- Coordination does not make cross-aggregate validation and commit one transaction.
- Coordination does not trust a stale query or event payload without target-side revalidation.
- WorkflowRun does not own Session lifecycle or call Issue synchronously for results.

## Status

The active coordination contract uses one write authority per business fact, one aggregate transaction per
command, and durable asynchronous closure for cross-aggregate effects.
