# WorkflowRun State Persistence

WorkflowRun persists neutral WorkflowActionAttempt orchestration records. Agent-backed attempts retain only
AgentJob and AgentSession references. AgentJob owns execution state.

This document defines the content boundary and read/write cost rules for persisted WorkflowRun State. Dispatch
snapshot semantics and storage lifecycle are defined in [`task-dispatch.md`](design.md).

## Design Drivers

WorkflowRun State is one authoritative decision record that is loaded and rewritten after state changes. Every
unnecessary byte increases observation and progress cost. Three boundaries follow:

- Current decision facts belong in State. Traceable history belongs in events.
- Redeliverable execution input belongs in a short-lived attempt snapshot. Copying it into State makes old
  attempts increase unrelated decision cost.
- Format compatibility belongs at startup migration. Historical shapes must not branch through the live read
  path.

The size budget is a correctness boundary, not only a storage optimization. An unbounded State record can make
observation and progress compete for memory and write capacity.

## Model

State is the persisted authority for one WorkflowRun. It contains only runtime facts required for current
scheduling, retry, recovery, and presentation decisions. The WorkflowRun state owner loads it when active and
rewrites it after each state change.

State is not:

- History. Event storage owns traceable history.
- The dispatch contract. Dispatch snapshots follow [`task-dispatch.md`](design.md) and do not enter State.
- Large content. Prompt bodies, complete task-output aggregation, and dispatch payloads are rebuildable or
  referenced and are not copied into State.

## Semantics

### Content Boundary

Every State field must answer a current question about scheduling, retry, recovery, locking, or presentation.
A field retained only for possible future use does not enter State.

Content that grows with task or retry count must be budgeted per WorkflowActionAttempt. An attempt carries
only fields required for its own decisions. It does not duplicate shared data such as every Prompt or all
earlier task output.

A superseded or terminal attempt does not retain a dispatch snapshot. [`recovery.md`](design.md) determines
recovery-chain attempt count. State adds no historical limit. Enforce size through the content boundary, not
truncation.

A normal active run must remain within hundreds of KiB. A State record above 1 MiB violates the content
boundary and is a defect, not a capacity request.

WorkflowRun State retains an Approval Feedback list capped at 10 entries total. An open feedback consumes one
slot. Eviction removes the oldest resolved entries and never an open entry. Rerun and `rerun-from-stage` discard stale
open feedback obligations when they replace or reset stage execution. Request facts remain in events.

Resolved feedback beyond the window is not archived in full. Events retain request and task-completion facts,
but resolution details of evicted cycles are not reconstructable. [`definition.md`](../definition/design.md#approval-feedback)
defines Approval Feedback and Feedback Tasks. This document defines only their State boundary.

### Read and Write Cost

The established shape rewrites complete State on every state change. State size multiplied by event frequency
is write amplification. The content boundary is the only control for it.

A complete read by run ID for report, dispatch, log, or control-plane status pays complete deserialization
cost. Callers must not use it as a cheap metadata query. A query needing only status or another scalar uses
projected columns without deserializing State.

Legacy JSON migration is a write-time obligation. A read path must not parse the complete document to probe
for migration.

### Format Evolution and Migration

A running Server recognizes one canonical State format. A per-record discriminator would create permanent
branching for a one-way startup upgrade, so multiple formats must not coexist on the read path.

A State format change is a database upgrade completed before the new Server accepts requests. The
verification-command cutover does not rewrite persisted task-attempt properties or a bound Workflow
Definition. Unknown historical properties are ignored by the current model. Existing active Runs without a
bound Definition are drained or stopped operationally before deployment. Terminal historical rows remain
readable.

```text diagram
                              yes  +--------+             +-----------+
                             +---->| Commit +------------>| Canonical |
+-----------+      +--------+|     +--------+             +-----------+
| Preflight +----->| Valid? ++
+-----------+      +--------+|no   +---------------+
                             +---->| Startup fails |
                                   +---------------+
```

Database initialization is the sole upgrade entry point:

- EF migration owns schema changes and unambiguous transformations expressible with SQLite JSON.
- An ordered, idempotent C# data upgrader in the same initialization owns transformations requiring Workflow
  semantics, structural comparison, or rejection of ambiguity.
- A rule must not be duplicated in SQL and C#.
- Pending EF migrations and data upgraders run in order across releases. Each migration converges one way.
  Historical support remains in cold-start migration and does not enter the current read model.

A data upgrader first performs a read-only preflight. It finds all candidates, converts them through one rule,
and deserializes each result with the current model. If any row is ambiguous or unreadable, it writes no
State. Startup fails and identifies the WorkflowRun.

After preflight succeeds, one database transaction writes all converted rows and increments each affected
ETag. Conversion does not filter by WorkflowRun lifecycle. `failed` supports retry and rerun and must migrate
without semantic change. A canonical row must not be rewritten.

A data upgrader is idempotent. A failed write rolls back with the transaction. The next startup reevaluates
persisted data and retries without process-memory progress. State migration is idempotent by persisted bytes
and must not rewrite a canonical record or advance its revision when content is unchanged.

Before a destructive production-State rewrite, create and verify a consistent database backup. Restore from
that backup instead of inventing a reverse transformation.

After migration, every read entry deserializes State directly with the current model. It neither probes
historical fields nor calls a converter. A database with incomplete migration cannot enter the service phase.
Acceptance requires zero historical-conversion calls on the read path. Migration code may remain at the
cold-start boundary while old database upgrade remains supported.

### Historical Interruption Compatibility

Canonical State has no interrupted Action status, task or Stage `WorkInterruption`, or `recoverable-interrupted` projection. These
shapes are legacy startup-migration input only. They are not a live recovery mechanism and cannot authorize
Runner redelivery, AgentJob launch, replacement execution, or a new deadline.

The cold-start upgrader classifies legacy status and interruption fields from raw JSON before normal
deserialization. It may remove an interruption or map an Action attempt to `Failed` only when persisted
terminal facts identify that exact owning attempt or checks batch. A terminal WorkflowRun or Stage alone is
not proof.

An active, mismatched, malformed, or otherwise ambiguous row fails startup with zero writes. The upgrader must
not invent a result, reason, timestamp, event, or state transition.

Removing these fields from current State does not remove stored `TaskInterrupted` or `ChecksInterrupted` events. They remain
readable history under [`event-protocol.md`](../../platform/events/spec.md#historical-workflow-interruption-events) and never
reconstruct current State.

## Status

Dispatch snapshots remain external to WorkflowRun State and follow [`task-dispatch.md`](design.md). List and
status reads use small projections and a versioned status cache. A cold-start upgrader converts historical
State before service traffic, so normal reads do not carry a legacy converter. Retention for events,
transcripts, and telemetry remains a database-wide lifecycle concern and does not expand WorkflowRun State or
its read path.
## Task Dispatch

Task dispatch is Workflow-owned work. `mohist/agent` tasks enter the durable AgentJob launch boundary described in
[launch convergence](../../session/input-and-turns/design.md#launch-convergence).

This document is the sole authority for evaluating task input templates. `tasks[*].with` and task-level `expect`
remain Workflow declarations. Server dispatches them unchanged. Runner evaluates them once at the execution
entry point against an immutable attempt snapshot. Runner never receives a Profile template expression:
`uses` is a literal Action name before dispatch.

### Design Drivers

- Server must preserve the authored Workflow declaration. It must not freeze rendered values into a TaskRun or
  dispatch payload.
- One Runner entry point must apply the same rendering and validation rules to ordinary, redelivered, retried,
  recovery, and rerun attempts.
- An attempt must use one immutable snapshot. Later Variable or Prompt changes affect only work that has not
  been dispatched.
- Actions receive one rendered, validated input channel. They do not receive raw declarations, resources, or
  the complete dispatch context.

### Model

A dispatch contains the original `with` and `expect` declarations plus a context snapshot for that
attempt. The snapshot contains Effective Stage Variables, Prompt bodies loaded by key, fixed Workflow and
runtime facts, and recovery failure facts when applicable.

The Prompt body, Effective Stage Variables, runtime context, and failure context are immutable for the
attempt. Runner creates a new rendered structure before manifest validation and the Action call. It never
mutates the original declaration, persisted task definition, `addTasks` definition, or retry source.

#### Rendering Boundary

Server persists original `with` and `expect` declarations with the task. It does not expand templates.
Every wire dispatch carries those declarations and an immutable snapshot containing:

- Effective Stage Variables, resolved at dispatch and frozen under [`variables.md`](../variables/spec.md).
- Project Prompt bodies, loaded by key at dispatch.
- Runtime facts: `workflow.runId`, `workflow.verification.command`, `stage.name`, `work.*`, `issue.*`, `repository.*`, `tasks.<id>.outputs.*`, and `workspace.*`.
- `failure.*` facts for a recovery task.

Before manifest validation and the Action call, Runner renders local inputs from the original `with` and
`expect` against that snapshot. Runner then validates the manifest, resolves `working-directory`, and invokes the
Action. The Action receives only rendered and validated input. No input channel exposes raw `with`, raw
`expect`, a Variables resource, or the complete dispatch context. `expect` remains a Workflow-owned
completion contract and does not enter the Action input channel.

```text diagram
+-----------------+    +---------------+    +-------------------+    +--------+
| Server dispatch +--->| Runner render +--->| Manifest validate +--->| Action |
+-----------------+    +---------------+    +-------------------+    +--------+
```

Once dispatched, the snapshot does not change. Later Variable or Prompt changes affect only Tasks not yet
dispatched and later attempts, including retry, recovery continuation, and `rerun-from-stage`. A Profile edit affects
only future WorkflowRuns because the current Run uses its complete bound Definition.

### Semantics

#### Template Expression Rules

Runner applies these rules to every attempt during rendering. Server dispatch does not evaluate expressions:

```text literal
${{ path }} occupies the whole value  -> replace it and preserve the JSON type
another resolvable expression         -> replace it from dispatch context
${{ prompts.<key> }}                  -> body was loaded by Project Prompt key at dispatch;
                                        evaluate it with the same syntax as `with` / `expect`
expression embedded in a string       -> convert the value to text and interpolate it;
                                        unresolved or object/array value -> task fails
any unresolved whole expression       -> task fails
ordinary value                         -> preserve it unchanged
```

The [Template Expressions](../definition/spec.md#template-expressions) product reference is
authoritative for author-visible interpolation and `\${{` escaping.

#### Deferred Rendering

`render: deferred` is declared on an input field in an Action manifest. Runner preserves a deferred field unchanged,
including internal runtime `${{ ... }}`, through manifest validation and the Action call. Fields without that
declaration are recursively expanded, including nested objects and arrays. An Action can read retained
internal templates only from a deferred field.

Runner resolves the Workspace exactly once for each WorkItem and provides it as `ActionContext.workDir`. An Action must not
select another directory from `variables.workspace.path`. That value is dispatch context, not a second execution entry point.

If a persisted WorkItem violates the selected Action's static `uses` or `with` contract, or if the
attempt snapshot cannot resolve a template, Runner returns deterministic `invalid-input`. The claimed TaskRun
reports that failure with the exact `workerId` and `workId`. Poll redelivery must not retry the same
deterministically invalid input.

#### Prompt Body Evaluation

A Prompt body is not persisted task input. At dispatch, Server loads the body identified by `prompts.<key>` into the
snapshot. Runner evaluates `${{ ... }}` inside that body during rendering with the same syntax and failure rules
as `with` and `expect`.

Redelivery, retry, and rerun each use their own dispatch snapshot. The Prompt body for an attempt is therefore
bound to its dispatch time.

#### Effective Variables Resolution

[`variables.md`](../variables/spec.md) defines Variables resources, cross-scope merging, and dynamic effects. Server
resolves Effective Stage Variables at dispatch and freezes them in the snapshot. Runner does not read a
Variables resource or fetch newer values after dispatch. `vars.*` appears exactly once during an attempt, in
that snapshot.

Runner expands `${{ failure.* }}` while constructing a recovery task because it holds the triggering task's output.
Other expressions, including unbound `vars.*` in that task, remain in the original declaration and are
expanded during the new attempt's rendering. See [`recovery.md`](design.md).

#### Dispatch Context

The [Template Expressions](../definition/spec.md#template-expressions) product reference defines
author-visible namespaces. Dispatch fixes `workflow.runId`, `stage.name`, `work.*`, `issue.*`, `repository.*`, and prior
`tasks.<id>.outputs.*` facts. Tasks produced by Approval Feedback also receive `work.approvalFeedback.*` facts.

Effective Stage Variables under `vars.*` and Project Prompt bodies under `prompts.<key>` enter the snapshot at
dispatch. Runner evaluates Prompt bodies during rendering and resolves `workspace.*` at the execution entry point.
Only recovery tasks receive `failure.output`, `failure.error.code`, and `failure.error.message`.

Runtime context, Workflow Variables, and Project Prompts are independent namespaces with distinct sources and
timing. Effective Variables appear only under `vars`; keys are not copied to bare top-level names. Runtime
context is not written to or merged into Variables. `work.approvalFeedback` exists only on a task produced by that feedback
and is absent from ordinary tasks. Plan-artifact paths are not runtime context. A Profile and Prompt express
them directly, for example `PLANS/PLAN.md`. See [Runner dispatch](../../runner/work-confirmation/design.md#dispatch-protocol-claim--pull--report) for the complete dispatch and report flow.

#### Dispatch Snapshot Persistence

An attempt snapshot is the contract actually dispatched. Its lifecycle is:

- The first write wins. Poll redelivery returns the exact original snapshot without rendering it again.
- The snapshot exists only while the attempt is Running, after dispatch and before a terminal report. It
  expires when the attempt becomes Completed, Failed, or Cancelled, or when a later attempt supersedes it.
- The snapshot is separate from WorkflowRun State. State contains arbitration facts and does not copy dispatch
  payloads. Historical attempt snapshots do not exist.
- A check dispatch does not persist a snapshot. Redelivery reconstructs it through the ordinary translation
  boundary.
- Content deduplication inside a snapshot, such as Prompt keys or on-demand task-output pruning, may reduce
  rendering content without changing this lifecycle. See [`variables.md`](../variables/spec.md).

#### Validation Timing

Catalog validation during Profile save or update checks constant inputs against the Action contract: unknown
`uses`, unknown input keys, missing required inputs, and constant type mismatches. For a template
expression, it checks only the key name. Server dispatch does not expand templates.

Runner renders expressions and then applies manifest value, type, and required-field validation. A failure
returns `invalid-input` and does not call the Action.

If a persisted WorkItem names a retired Action, Runner rejects it during dispatch. Manifest validation finds
the tombstone and returns its guidance as a non-retryable error.

#### Parent Context for a Child-Issue Plan

Dispatch carries no parent payload. A child-Issue Plan `mohist/agent` task starts from the child Issue body,
which remains the only delivery-scope authority. The child knows its parent through the `parent` reference, and
the Agent reads the parent Issue through the `mo` CLI when the work needs shared context. Nothing about the
parent is persisted in WorkflowRun State, task input, Variables, or Prompts, and no template namespace exposes
it.

WorkflowRun stores its bound Repository snapshot and write-once Pull Request identity. Neither is a Run
Variable. The Issue's Repository binding supplies the snapshot at start, and dispatch uses that stable
Repository context. The first `github.pr.number` carrier through the Workflow grain records Pull Request identity. The
same number is accepted again. A conflicting number is rejected. See [`../repositories.md`](../../project-space/repositories/design.md).

### Status

Active attempt snapshots are stored outside WorkflowRun State. The first dispatch fixes the snapshot,
redelivery reuses it, and terminal or superseding transitions remove it. Startup removes orphaned snapshots.
Arbitration therefore depends on current execution facts instead of payload history.
## Aggregate Coordination

Issue and Epic belong to the Issue context. WorkflowRun belongs to the Workflow context. AgentJob, Runner, and
Session belong to Agent execution. This document defines their cross-context coordination without creating a
second owner for any business fact.

In the diagrams, `->` is a synchronous command and `[Event]` is an asynchronous reaction started by a
durable handler after commit. Each solid command enters exactly one target aggregate transaction. A command
caller and target do not share a transaction.

### System Boundary

Cross-aggregate workflows use a synchronous command to commit one fact and a durable event to close the
business loop. A query may be stale, but the target aggregate revalidates its current state before committing.
A synchronous call stack must never cycle back into an aggregate that is already calling.

### Write Authorities

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

### Association and Migration

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

### Epic Advances Issues

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

### Workflow Results and Continued Advancement

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

### Synchronous Direction and Asynchronous Closure

Aggregates may depend on each other in both directions, but each call stack has one direction:

- Association is Epic -> Issue. Issue does not call Epic synchronously from that command.
- Advancement is Epic -> Issue. Issue starts Workflow and notifies Epic through events.
- Execution result is WorkflowRun -> event -> Issue command. WorkflowRun does not call Issue synchronously.
- Affiliation refresh is Issue -> event -> Epic and WorkflowRun. Neither target calls Issue back from the
  command.

These paths form a business loop, but each commit contains exactly one aggregate.

### Session Ends Bound Workflow Work

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

### Session and AgentJob Propagate One Way per Call Stack

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

### Other Interactions

Two additional one-way interactions exist:

- Issue -> Cancel -> WorkflowRun.
- Runner -> `[RunnerDisconnected]` -> Session, which fails affected Sessions.

### Non-Goals

- Coordination does not create an independent membership, owner, or controller aggregate.
- Coordination does not make cross-aggregate validation and commit one transaction.
- Coordination does not trust a stale query or event payload without target-side revalidation.
- WorkflowRun does not own Session lifecycle or call Issue synchronously for results.

### Status

The active coordination contract uses one write authority per business fact, one aggregate transaction per
command, and durable asynchronous closure for cross-aggregate effects.
## Plan Artifacts

The default Workflow separates free-form planning from machine execution without creating a
second planning concept. Everything produced by Plan is a run artifact. Exactly one artifact,
`PLANS/tasks.json`, is machine-readable and expands into Build Tasks.

### Design Drivers

- The Agent needs freedom to organize planning material, while the Workflow needs one stable
  machine input.
- Evidence must remain inspectable without becoming an execution channel.
- Plan and Check need one Approval Point mechanism, not parallel self-review and repair
  protocols.
- A rebuildable Workspace requires explicit recovery for Repository work and an accepted loss
  boundary for local plan material.

### Model

Plan has one execution path and one evidence path:

```text diagram
                      +------------+    +-------+
                  +-->| tasks.json +--->| Build |
+------+          |   +------------+    +-------+
| Plan +----------+
+------+          |   +-----------+     +-------------------+
                  +-->| Artifacts +---->| Approval evidence |
                      +-----------+     +-------------------+


+----------------+    +--------------------------+
| Home unavailable+--->| Provision from bound    |
+----------------+    | artifacts and Git branch |
                      +--------------------------+
```

`tasks.json` is the machine path. Other artifacts are the evidence path. If the Workspace Home is
unavailable, provisioning makes the bound artifacts available before Build reads `tasks.json`.

#### The Task List

`PLANS/tasks.json` has this shape:

```json
{
  "tasks": [
    {
      "id": "T-001",
      "title": "Extract the notification-channel abstraction",
      "goal": "What to implement and why, in a few sentences.",
      "acceptance": ["verifiable criterion"],
      "refs": ["PLANS/DESIGN.md#abstraction"]
    }
  ]
}
```

The engine consumes only `id`, `title`, and array order. `mohist/task-list` expands each entry
into one Agent Task in array order through the existing `addTasks` mechanism. The Profile fixes
the execution Action for every generated Task.

The Agent receives `goal`, `acceptance`, and `refs` as prompt material. The Workflow does not
verify acceptance mechanically. Build verify Tasks, Check evidence, and the approver's judgment
provide verification.

The schema has no other fields. It has no per-task `expect`, `uses`, priority, type, mode,
or dependency graph. Ordering metadata is not rendered as prompt text.

The task list is an ordinary artifact file. WorkflowArtifact carries its evidence and audit
role. `addTasks` carries its expansion. The Workspace directory carries its persistence. No
dedicated entity, channel, or lifecycle exists for the task list.

#### Why no mechanical per-task assertions

Per-task `expect` markers duplicate Stage verification and make self-review depend on
promise-marker parsing. Hard checks belong in explicit verify Tasks that run real tests, not in
generated file-existence assertions.

#### Named Artifacts

The Workflow binds four files as Task artifacts so Approval Points and later Stages have stable
evidence:

- `PLANS/PLAN.md`: interpretation, scope, approach, and the Plan Approval Point document.
- `PLANS/DESIGN.md`: technical decisions and rationale. It always exists. When no separate design is
  needed, it records that conclusion and why.
- `PLANS/REVIEW.md`: Check Stage review evidence.
- `PLANS/tasks.json`: the machine-readable task list.

Everything else under `PLANS/` and `RESEARCH/` remains the Agent's organization. The Workflow
does not consume it.

### Semantics

#### Persistence and Recovery

A WorkflowRun uses one Workspace identity across Stages. The Runner provisions a Workspace Home
before each task and preserves a valid existing Home. If the Home is unavailable, provisioning
uses the remote Workflow branch for Repository contents and the current WorkflowRun's bound
artifacts for declared non-repository files. Build then reads `PLANS/tasks.json` from the local
Home as usual.

Artifact upload serves evidence and audit after a task report is accepted. Bound artifacts are
also durable inputs for Workspace Home provisioning. Pending uploads are not durable and cannot
be used for provisioning.

Only declared non-repository paths are provisioned from artifacts. Unpushed Repository work,
undeclared Workspace files, and `.scratch/` remain outside the persistence boundary.
See [`../workspaces.md`](../../workspace/lifecycle/design.md).

#### Review at an Approval Point

The Workflow has one review mechanism: an Approval Point with its Approval Feedback sequence.

- Plan has no self-review Task. A same-session self-verdict would duplicate the Plan Approval
  Point and add a second repair loop.
- Check runs an independent review in its own Session and records evidence in `PLANS/REVIEW.md`. It has
  no verdict marker, PASS/FAIL gate, or auto-fix recovery loop. The approver owns the verdict.
- A Request Changes decision uses the configured Feedback Tasks as the single repair path. See
  [`definition.md`](../definition/design.md#approval-feedback).
- Approval Feedback permits unlimited Request Changes cycles. State retains at most 10 feedback
  entries. Unattended recovery loops keep their declared budgets.

#### Integrate: Auto-merge

Integrate enables GitHub auto-merge on the approved Pull Request. The registration Action waits
until GitHub reports the merge.

One attempt has a fixed 30-minute absolute deadline covering subject selection, every external
operation, ambiguous-registration reconciliation, and retry delays. An explicit squash subject
wins. Otherwise the Action uses the Pull Request title from its bounded read.

GitHub arbitrates merge timing and merge-time prerequisites. The Workflow therefore has no
`base-moved` or `protection-conflict` recovery branches. A required check that fails after approval is
`pr-checks-failed` and uses the Check recovery. A merge conflict is `conflict` and uses rebase recovery.
Enabling auto-merge on a Repository that disallows it is an ordinary Task failure, `auto-merge-unavailable`.

`retry-safe` permits a later explicit retry. It does not authorize unattended recovery.
Cancellation remains cancellation. The one-shot `github-pr-status` Stage Check with `expect: merged` remains
post-hoc verification.

`mohist/merge-github-pr` is removed. No consumer remains.

#### Prompt Realignment

Built-in prompts follow the artifact boundary:

- Removed: `proposal`, `specs`, `design`, `tasks`, `self-review`, `fix-plan-review`, and `auto-fix`.
- Added: `plan`, which produces the named artifacts including the task list, and `build-task`,
  which is the base prompt for generated Tasks.
- Repurposed: `review` writes evidence without a verdict marker. `apply-feedback`, `fix-ci`,
  `fix-pr-checks`, and `resolve-rebase-conflicts` retain their roles with `PLANS/` paths.

#### Web Evidence Surface

The Check Approval Point UI reads the recorded `REVIEW.md` artifact rather than Task output. The
Plan Approval Point surface presents `PLAN.md` and the task list. Each Approval Point therefore
shows the artifacts produced by its own Stage.

#### Out of Scope

A Profile-declared automatic reviewer such as `reviewer: agent` is outside this contract. An Approval
Point is a judgment position. Whether a person, External Agent, or automation supplies that
judgment remains outside the engine.

#### Companion Requirement

Plan and review evidence exists only as uploaded run artifacts. The Web artifact surface exists.
Delegated approvers also need `mo run artifact list/get`, because they previously read plan files from the
Workflow branch.

### Status

The default Workflow uses `tasks.json` as its only machine-readable planning input. Named plan and
review artifacts are uploaded for Approval Point evidence and are available as durable inputs when
a Workspace Home must be provisioned again. Auto-merge and prompt realignment follow the
boundaries above.
## Task Recovery

Workflow recovery chooses which follow-up Action to schedule. AgentJob owns physical Agent execution recovery.
A recovery `mohist/agent` Action creates a new AgentJob.

The Runner executor matches `when` expressions against the `{ output, error }` result context, builds recovery tasks,
and returns them through `addTasks` for mechanical insertion by the engine. Recovery is part of task
completion, not only failure remediation. Explicit matching is independent of task success or failure:
successful output that matches `when: output.promise=FAIL` also triggers recovery. The default handler, which omits `when`,
handles only results with an error, including final failures produced after the Action completes.

Author-visible syntax and semantics, including budget, first match, `retrySelf`, and manual retry, are defined
in [`recovery`](../definition/spec.md#recovery-failure-recovery). This document defines execution.

### Design Drivers

- The engine remains generic. It understands Stage, task, check, completion, and failure. It treats `recovery`
  as an opaque task attribute.
- Workflow YAML remains read-only during execution. Remaining budget is per-attempt state in `recoveryRemaining`, not a
  modified configuration copy.
- Recovery tasks are real Workflow tasks. They appear in the graph, timeline, and state.
- A task that triggers recovery is completed because it produced later work.
- Runner owns matching and task construction. Actions do not interpret recovery.

### Model

Workflow YAML declares `budget` and `handlers`, with optional `when`, `tasks`, and `retrySelf`. The Action
returns output or an error and has no recovery awareness. The engine inserts returned `addTasks` mechanically
and passes `recoveryRemaining` as opaque per-attempt state.

Runner matches explicit `when` handlers first and then the default handler when an error exists. It maps
explicit `null` to the full `budget`, clamps numeric values to the declared range, and builds `addTasks`
from the original declaration. A handler task receives its own full `recoveryRemaining`. A `retrySelf` copy receives the
remaining budget minus one.

Runner expands only `${{ failure.* }}` references while constructing a recovery task. Other expressions remain in the
new task declaration and are evaluated at that attempt's dispatch entry point. See
[`task-dispatch.md`](design.md).

A `retrySelf` task copies the triggering attempt's original dispatch declaration, not this Action execution's
rendered input. The copy includes `with`, task-level `expect`, artifacts, `setVars`, recovery
configuration, and task identity. Only `recoveryRemaining` changes as separate state. A later dispatch can therefore
evaluate `${{ vars.* }}` against its own Variable snapshot instead of freezing values from the triggering attempt.
Every new attempt expands its declaration against its own context snapshot.

### Semantics

#### Remaining Budget (`recoveryRemaining`)

A recovery budget bounds one continuous automatic-recovery round. `recoveryRemaining` travels with the task as execution
state:

```text diagram
+------+    +------+    +------+    +--------+    +-----+
| YAML +--->| Task +--->| Work +--->| Runner +--->| Add |
+------+    +------+    +------+    +----+---+    +--+--+
                ^                        |           |
                +------------------------+-----------+
```

Runner `tryRecovery` is the sole read and write authority for `recoveryRemaining`. The engine passes the field through and
never reads its value. On the engine side, the field is side-channel state during task intake, following the
`causedByFeedbackId` precedent. It never enters `TaskDefinition`.

An explicit `null` starts a new round with the full `budget`. An absent field is malformed transport and
receives ordinary-result handling. It must not reopen the budget. A matched handler consumes one unit of
budget. An unmatched result consumes none. The declaration is never modified.

#### Manual Retry Opens a New Round

A manual retry reconstructs the Task from its original declaration and definitional fields. It starts with
`recoveryRemaining = null`, so Runner opens a new round from the declared `budget`. Corrected Variables and Prompts can
therefore take effect without turning execution output into future configuration. The failed attempt and its
consumed budget remain unchanged for audit.

#### A Stage Rerun Does Not Reuse TaskRun Identity

A `TaskRun` identity consists of Definition ID, Stage attempt, and task attempt. The first Stage attempt
retains `{definitionId}.{taskAttempt}`. Later attempts use `{definitionId}.s{stageAttempt}.{taskAttempt}`. For example, the first build task is `T-001.1`, a manual
retry in the same Stage is `T-001.2`, and the first task after rerunning build is `T-001.s2.1`.

Definition IDs are scoped to a Stage. If a candidate TaskRun ID already exists in the WorkflowRun, the
allocator appends the first available `.runN` suffix. This preserves established IDs without collisions and
keeps every persisted TaskRun ID and Work ID unique within the run.

`rerun-from-stage` discards visible task history from the old Stage, but it cannot decrease the Stage attempt or reuse
an old identity. A Workflow AgentSession whose default name is the Work ID is always a new logical Session. It
cannot inherit the invalidated attempt's physical binding or working directory. An explicit `session` name
retains its own reuse semantics through the Workflow Definition.

#### Runtime Binding Repair Is Not Workflow Recovery

Before submitting independent input, AgentSession may find that its Runtime Session is confirmed missing and
repair the physical binding under [`agent-execution.md`](../../session/recovery/design.md#runtime-session-missing-recovery). Repair
occurs before the Action produces a result. A successful repair continues the original TaskRun attempt without
a recovery task, budget decrement, or manual Retry. A failed repair passes a normalized error into the
recovery and manual-retry rules here.

WorkflowRun neither decides nor implements Runtime binding repair. Runner reports Runtime facts, Session
arbitrates and persists the binding, and Workflow interprets only the final Action result.

#### Runner Executor Flow

The Runner executor applies these rules in order:

1. Build `{ output, error }` from the Action result.
2. Return the ordinary result when `recoveryRemaining` is absent.
3. Give explicit `null` the full declared `budget`. Clamp numeric values to the declared range.
4. Match the first explicit `when` handler. If none matches and an error exists, match the default handler
   without `when`.
5. If a handler matches and budget remains, bind `${{ failure.* }}` in its tasks, copy the original declaration for
   `retrySelf`, and return completed with `addTasks`. Handler tasks receive full budget. The retry copy receives
   the remaining budget minus one.
6. Without a matching handler, return completed when there is no error and failed otherwise.

At most one default handler exists and it is last, so it cannot shadow an explicit match. It matches after the
executor forms the final failed result, including failures such as a dirty workspace or invalid branch.
Negative `recoveryRemaining` clamps to 0. Values above the declared limit clamp to that limit.

A recovery handler may read `${{ failure.output.* }}`, `${{ failure.error.code }}`, and `${{ failure.error.message }}`. The message carries an actionable error into
a recovery task. A handler must not branch on the message.

#### WorkResult

```text literal
{
  "status": "completed",
  "addTasks": [
    { "id": "recover:rebase", "uses": "mohist/rebase", "with": {...} },
    { "id": "load-tasks", "uses": "mohist/task-list", "with": {"path": "PLANS/tasks.json"}, "recovery": {"budget": 2, ...}, "recoveryRemaining": 1 }
  ]
}
```

`completed` with `addTasks` makes the engine insert tasks into the current Stage. `completed` without `addTasks` is
normal completion. `failed` fails the Workflow.

#### One-Off Task Injection Uses the Profile Mechanism

An API-triggered rebase task carries a `RecoveryDefinition` and uses the same budget, handler matching, `when`,
`retrySelf`, and remaining-budget semantics as inline `task.recovery`. Only the trigger differs: the API route selects
the recovery and the Runner executor applies it. Runner still matches and decrements the budget, and `addTasks`
still follows the engine insertion path.

One-off injection does not have a second representation or different namespaces. Recovery content remains a
`RecoveryDefinition`, so application code selects a name rather than copying `uses`, Prompt references, budget, or
handler order.

#### Top-Level `recoveries`: Named Recovery Templates

The top-level `recoveries` key owns named recovery templates. A one-off trigger selects a template from the
complete Workflow Definition bound to the WorkflowRun. It does not read the current Profile or build a second
definition in application code.

```yaml
recoveries:
  rebase-conflicts:
    budget: 2
    handlers:
      - when: error.code=conflict
        tasks:
          - id: recover:resolve-rebase-conflicts
            title: Resolve rebase conflicts
            uses: mohist/agent
            with:
              name: mohist/builder
              session: check
              prompt: ${{ prompts.resolve-rebase-conflicts }}
        retrySelf: false
```

The `recoveries` map is part of WorkflowDefinition and round-trips with the Profile. Workflow content remains the
single author of recovery `uses`, Prompt references, budget, and handler order. A Profile edit therefore
changes future WorkflowRuns in one place.

Template names are lowercase and hyphen-separated, such as `rebase-conflicts` and `plan-conflicts`. Each built-in YAML file
declares the templates it needs. Extract cross-Profile sharing only after a third Profile or a second shared
template appears.

### Status

Implemented: `retrySelf` and manual retry reconstruct the original declaration. `with` and `expect` retain
Workflow expressions, new attempts evaluate them against their own context snapshot, and `recoveryRemaining` remains
separate execution state.
