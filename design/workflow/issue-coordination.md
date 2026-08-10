# Aggregate Coordination

Participants are `Issue` and `Epic` from the Issue context, `WorkflowRun` from the Workflow
context, and `Runner` and `Session`.

In the diagrams, `->` denotes a synchronous command and `[Event]` denotes an asynchronous reaction
triggered by a durable handler after commit. Each solid command enters exactly one target aggregate
transaction; it does not mean that caller and target share a transaction.

## Write Authorities

| Business fact | Sole write authority | Use by other participants |
|---|---|---|
| Which Epic currently contains an Issue | Issue.`EpicNumber?` | Epic queries Issue; WorkflowRun stores minimal run context |
| Epic lifecycle and advancement policy | Epic | Issue carries only `EpicNumber?` and does not copy Epic state |
| Issue lifecycle and current WorkflowRun | Issue | Epic queries it; WorkflowRun results return through events |
| Workflow execution state | WorkflowRun | Issue stores only the current `WorkflowRunId` |
| Runner presence and capacity | Runner | Workflow scheduling consumes only its public facts |
| Session lifecycle | Session | WorkflowRun and Agent store only associated identities |

There is no independent membership aggregate, generic `OwnerRef`, or controller aggregate. Member
lists, progress, and the next candidate Issue are queries over current Issue state, not a second set
of facts that Epic can modify independently.

## Association and Migration

```text diagram
User / API -> Epic.LinkIssue(issueNumber)
              Epic reads the Issue's current affiliation
              Epic verifies it is not closed for a new association
              Epic -> Issue.AssignEpic(epicNumber)
                         |
                         +-- transaction: Issue state + [IssueEpicChanged]

[IssueEpicChanged]
  +-> old Epic.Recompute
  +-> new Epic.Recompute
  +-> active WorkflowRun.UpdateIssueContext(current Issue context)
```

`LinkIssue` first reads the Issue's current affiliation. If the Issue already belongs to this Epic,
it returns success even if the Epic became `closed` after the original request committed. A retry
cannot turn a successful result into failure. Only an unassociated Issue causes `LinkIssue` to
check `closed` and send a write command.

In one Issue transaction, `AssignEpic` changes `EpicNumber?` from the old value to the new one.
Moving an Issue therefore needs no unlink-then-link sequence and has no intermediate state in which
two Epics own it. Assigning the same number again is a no-op. Disassociation uses
`Issue.RemoveEpic(expectedEpicNumber)`. If the Issue has already moved to another Epic, a late
command from the old Epic cannot clear the new affiliation.

Epic validation and the Issue affiliation commit are not one transaction. If the Issue commits but
the call result is lost, retrying `LinkIssue` returns the same idempotent result. If the subsequent
Epic state save fails, the durable `IssueEpicChanged` reaction still causes Epic to recompute. A
`done` Epic reopens when an open Issue joins, and an old Epic updates progress when a member leaves;
both converge through `Recompute`.

A handler does not treat an old event payload as current affiliation. It rereads current Issue
state before sending a complete command to Epic or WorkflowRun. Out-of-order delivery and
redelivery therefore cannot write an old Epic number back.

## Epic Advances Issues

```text diagram
User -> Epic.Start
          |
          +-- transaction: Epic state + [EpicStarted]

[EpicStarted] -> Epic.Advance
Epic.Advance -> query current Issues -> candidate Issue.TryStartFromEpic(epicNumber)
                                           |
                                           +-- transaction: Issue state
                                               + [IssueWorkStarted]

[IssueWorkStarted] -> WorkflowRun.EnsureStarted(
                        workflowRunId,
                        { ProjectId, IssueNumber, EpicNumber? })
                          |
                          +-- transaction: WorkflowRun state + WorkflowRun events
```

The candidate query performed by Epic may be stale. `TryStartFromEpic` must recheck the current
`EpicNumber`, state, dependencies, and existing WorkflowRun inside Issue. If the candidate is no
longer valid, Issue rejects the request or returns a no-op and Epic selects again later.
Correctness does not depend on atomicity between the query and command.

Issue allocates and stores `WorkflowRunId` in its start transaction without writing WorkflowRun.
The durable `IssueWorkStarted` handler rereads Issue and calls `EnsureStarted` only if the event
still refers to the current active run. WorkflowRun is created idempotently by `WorkflowRunId` and
enters its normal lifecycle directly. It needs no `AwaitingBinding`, `WorkflowBindingPending`, or
lineage revision. The same event redelivery recovers failures before creation, after creation but
before the response, or before handler acknowledgement.

## Workflow Results and Continued Advancement

```text diagram
Runner -> Report -> WorkflowRun
                    +-- transaction: WorkflowRun state + [WorkflowRunCompleted]
                    +-- transaction: WorkflowRun state + [WorkflowRunFailed]

[WorkflowRunCompleted] -> Issue.Complete(expectedWorkflowRunId)
[WorkflowRunFailed]    -> Issue.AbortWork(expectedWorkflowRunId)

User -> Issue.MarkDone
          +-- require leaf Issue in InProgress
          +-- require bound WorkflowRun status Stopped or Completed
          +-- transaction: Issue state + [IssueCompleted(completionKind=manual)]

[IssueCompleted / IssueCancelled]
  +-> Parent Issue.RecomputeComposite       (sub-Issue only)
  +-> current Epic.Advance                  (when affiliated)
```

Issue uses `expectedWorkflowRunId` to reject a late result from an old run. The next Epic advance is
triggered only by the terminal event committed by Issue; WorkflowRun never modifies Epic directly.

Manual completion is an explicit Issue lifecycle command. It neither fabricates
`WorkflowRunCompleted` nor modifies WorkflowRun. Before commit, IssueGrain reads the state of the
currently bound run. Only `Stopped` and `Completed`, which cannot be scheduled again, are accepted.
A `Failed` run can still be retried, so the user must stop it explicitly first. Because the allowed
values are terminal, the read cannot race with resume or retry. The Issue aggregate rechecks that
it is still `InProgress` and still bound to the same run, then writes the sole `IssueCompleted`
fact. The event's `completionKind` distinguishes `workflow` from `manual`; parent Issues, Epic,
Inbox, and metrics continue to consume the same completion event.

A duplicate command against `Done` is a no-op, so redelivery after a lost response cannot produce a
second completion event. A parent Issue with sub-Issues cannot be completed manually; only a fresh
snapshot of its sub-Issues determines its terminal state.

## Synchronous Direction and Asynchronous Closure

Aggregates in the same context may depend on each other in both directions, but a synchronous call
stack must never form a cycle:

- Association command: Epic -> Issue. Issue does not synchronously call Epic back from that call.
- Advancement command: Epic -> Issue. Issue starts Workflow and notifies Epic through events.
- Execution result: WorkflowRun emits an event, then an Issue handler sends a command. WorkflowRun
  does not synchronously call Issue.
- Affiliation refresh: Issue emits an event, then the handler updates Epic and WorkflowRun. Neither
  target aggregate calls Issue back from the command.

These paths form a business loop, but each call stack has one direction and each commit still
contains exactly one aggregate.

## Session Ends Bound Workflow Work

```text diagram
Session settles an active Turn with a non-success terminal outcome
  Session -> WorkflowRun.AbandonActiveWork(runnerId, workId, reason)
               |
               +-- transaction: WorkflowRun state
```

A Workflow-origin Session is bound to one WorkflowRun work item by `(runnerId, workId)`. When the
Session's active Turn settles with a non-success terminal outcome — an intended stop recorded
`Cancelled`, or a Runtime-reported failure — Session synchronously abandons that bound active work
with the settlement reason. The command enters exactly one WorkflowRun transaction and carries the
frozen identities recorded at settlement, so a late or replayed command cannot abandon work that
appeared later.

WorkflowRun never calls Session back synchronously from this command; each call stack keeps one
direction under
[Synchronous Direction and Asynchronous Closure](#synchronous-direction-and-asynchronous-closure).
If the command result is lost after the Session settlement committed, replaying the same settlement
operation re-issues the same abandon; the WorkflowRun command is idempotent on its frozen
`(runnerId, workId)`.

## Other Interactions

```text diagram
Issue -> Cancel -> WorkflowRun
Session -> AbandonActiveWork -> WorkflowRun
Runner --[RunnerDisconnected]--> Session (fails affected sessions)

WorkflowRun: Pause, Resume, Approve, Reject, Retry, Rerun
Issue: MarkDone, Archive, Unarchive, Reopen, Close
Runner: Register, Unregister, HeartbeatRepair
```
