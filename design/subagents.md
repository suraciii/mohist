# Subagent and Session Tree Design

A session tree is an optional parent-child relationship between
`AgentSession` instances. A running Mohist Agent can use it to launch another
Agent at runtime. It does not create an Agent hierarchy, a new message model,
a Session terminal state, or a Workflow orchestrator.

Product behavior is defined in [`../docs/subagents.md`](../docs/subagents.md).
The base lifecycles of `AgentJob`, `AgentSession`, `SessionInput`, and
`AgentTurn` remain defined in [`agent-execution.md`](agent-execution.md). This
document defines how those resources compose during a spawn, report, stop, or
detach.

```text diagram
+--------------------+   +--------------------------+
| Child AgentSession |   | SessionTreeMutationFence |
+----------+---------+   |     (Project-scoped)     |
           |             +-------------+------------+
           +------+--------------------+------+
                  vowns                       vfreezes membership
        +-------------------+   +--------------------------+
        | SessionParentLink |   | SessionTreeStopOperation |
        +---------+---------+   |         snapshot         |
                  |             +--------------------------+
                  |
                  vreferences
       +---------------------+
       | Parent AgentSession |
       +---------------------+
```

Scheduled input is an ordinary AgentSession capability, not a tree capability.
Its design is authoritative in [`scheduled-input.md`](scheduled-input.md). This
document records only where a schedule meets spawn, stop, or detach.

## Core Decisions

- Agent resources remain flat within a Project. `Subagent` is only the role of a
  child AgentSession in a parent-child relationship.
- A child delegation is an ordinary Agent launch: one `AgentJob`, one
  `AgentSession`, its first `SessionInput`, and its first `AgentTurn`.
- The child-owned `SessionParentLink` is the topology authority. The parent
  stores no mutable child list.
- A Project-scoped mutation fence orders topology publication and freezes stop
  membership. It does not become a second topology model.
- `AgentSession.Source` explains why a Session was created and never changes.
  Parentage is separate and detach cannot rewrite Source.
- Server resolves capability, identity, `Workspace`, `Materialization`, and
  Runner binding. The Runner executes only the resolved and pinned child work.
- Parent-to-child and child-to-parent messages use ordinary `SessionInput` and
  `AgentTurn` paths. The tree adds no inbox, message aggregate, or transcript
  branch.
- The tree exposes spawn, inspect, notification, stop, and detach primitives.
  Fork-join, waiting policy, retries, task decomposition, recommendations, and
  acceptance remain decisions of the Agent's Instructions and Skills.

## System Boundary

- **Agent configuration** owns the ordered set of allowed target Agent IDs. It
  does not copy target Instructions, Runtime, Model, Skills, or concurrency
  configuration.
- **AgentSession** owns its optional parent link, current binding, binding
  epoch, Inputs, Turns, and child-facing launch facts. Only the child writes
  its parent link.
- **SessionTreeMutationFence** owns mutation ordering, graph revisions,
  reservations, pending mutation commands, and stop-operation coordination. It
  never writes a child link or issues the parent's binding receipt.
- **Parent AgentSession** owns `CurrentBinding` and the durable receipt that
  reserves that binding for an attachment. The fence, child, and Runner cannot
  write or issue that receipt.
- **AgentLaunchCoordinator** owns spawn idempotency, pre-plan validation,
  launch-plan recovery, and provisional artifact cancellation. It extends the
  existing Agent launch pipeline rather than creating a second launcher.
- **Runner** receives a resolved prompt, WorkDir, Runtime, and binding
  constraint. It does not select a parent, resolve capability, or materialize
  an arbitrary path.

## Model

### Capability declaration and launch snapshot

An Agent definition stores `AllowedSubagentAgentIds`, an ordered set of stable
Agent IDs in the same Project. It stores IDs only. It does not copy the target
Agent's Instructions, Runtime, Model, Skills, or concurrency configuration.

Every Agent launch resolves that declaration into an immutable
`AllowedSubagentSnapshot`:

```text literal
AllowedSubagentSnapshot
  AgentId
  NameAtLaunch
  DescriptionAtLaunch
```

The snapshot is part of the parent `AgentJob` execution definition and is
written to the startup settings of the `AgentSession` it creates. A follow-up
in the same AgentSession does not resolve the capability declaration again.
Server places the Session's snapshot in startup context, not in an environment
variable or temporary client input.

The following rules define snapshot authorization:

- Configuration stores only the target's stable ID, and the target must belong
  to the same Project.
- A target rename after parent launch leaves the old name and description in
  the snapshot. Spawn may use a current name or ID only when it resolves to the
  same Agent ID.
- A target archived after parent launch rejects an unaccepted spawn with
  terminal pre-plan result `target_agent_archived`. A child already accepted by
  the launch coordinator is not revoked by later archival.
- Restoring the target permits new spawns for its declared ID. Existing
  snapshots do not change.
- Self-spawn is allowed only when this Agent's stable ID is explicitly declared.
  It uses ordinary launch scheduling.
- Parent Session, target Agent, child Job, and child Session must all belong to
  the same Project. Cross-Project spawn is always rejected.
- Archiving does not automatically remove an Agent ID from configuration. The
  declaration retains deterministic meaning if the Agent is restored, but an
  archived target never accepts a new child launch.
- Agent deletion is outside this design.

### SessionParentLink

A child AgentSession owns at most one optional `SessionParentLink`. The child is
the sole write authority because only the child can gain or lose its one parent.
Attaching an existing Session, reparenting, and reattaching are unsupported.

```text literal
SessionParentLink
  EdgeId
  ParentSessionId
  ParentAgentId
  ChildLaunchJobId
  AttachedAt
  AttachedRevision
  State: attached | detached
  DetachedAt?
  DetachedRevision?
  TerminalReport: none | pending | delivered | suppressed
  TerminalReportDeliveredInputId?
```

`ChildLaunchJobId` is the initial AgentJob for this delegation. It is not a
terminal marker for the child Session. The link transitions only from
`attached` to `detached`.

These invariants follow directly from child ownership and immutable target:

- A child has at most one current parent.
- A new child has no ancestor before its link is established. The link target
  never changes, so attachment cannot create a cycle.
- After detach, the child and its still-attached descendants form another tree.
  Source, WorkDir, Runtime binding, transcript, and AgentJob remain unchanged.
- The historical link retains ParentSessionId, EdgeId, DetachedAt,
  DetachedRevision, and delivered parent InputId for audit.
- Default tree queries return attached edges only.

### Tree mutation fence and graph revision

Each Project has one durable `SessionTreeMutationFence`. It is the sole
linearization point for attachment, detach, and cascade-stop snapshots. It
maintains a strictly increasing `GraphRevision`.

The fence holds only short-lived coordination facts: `LinkReservation` values
for unfinished plans, pending mutation commands, and the transaction needed for
one mutation. A reservation does not create a visible edge, change
`GraphRevision`, or count as an in-flight topology mutation.

```text literal
LinkReservation
  EdgeId
  ParentSessionId
  ChildSessionId
  State: reserved | attached | rejected
  RejectionReason?
```

Only a revision-assigned `AttachAwaitingAck` or `DetachAwaitingAck` is an
in-flight mutation. That includes a participant receipt that the fence has not
yet published.

The parent AgentSession is the binding authority. It owns `CurrentBinding`, an
internal `BindingEpoch` that increments on each establishment or replacement,
and a durable `BindingUseReceipt`.

Before `BeginFinalize`, the coordinator calls
`AcquireChildAttachBinding` on the parent with the complete expected binding,
epoch, WorkDir, command, and edge. The parent compares those facts and writes
the held receipt in one transaction. Reset and every binding replacement also
compare expected binding and epoch. They reject with
`binding_attach_in_progress` while a held receipt exists. If Reset linearizes
first, acquire returns a mismatch and the plan rejects or aborts with
`parent_binding_changed`. If acquire linearizes first, Reset cannot replace the
binding.

The complete pending attach tuple and participant receipt contain Project,
command ID, edge, parent and child Session, child launch Job, parent WorkDir,
RunnerId, Runtime, runtimeSessionId, BindingEpoch, BindingUseReceipt ID,
expected link state `absent`, and assigned revision. The detach tuple contains
command ID, edge, parent and child Session, child launch Job, expected attached
revision, and assigned revision.

The mutation order is fixed:

`Acquire -> BeginFinalize -> child exact attach -> acknowledgement -> Commit -> Release`.

`BeginFinalize` revalidates the held receipt, reservation, and stop admission
before assigning a revision. The child writes its link, index, and exact receipt
in one child transaction. The fence records acknowledgement only when every
field matches, then commits with the same command, edge, and revision. The
parent may idempotently release its receipt only after publish or durable abort.
A reset after publish is subsequent work and cannot revoke an attached plan.
Replaying the same acquire command returns the original receipt. Coordinator
activation recovery replays the same order from the held receipt. Receipts do
not expire by time. A receipt or child mutation mismatch remains held until
reconciliation instead of being released prematurely.

A child replay of the same tuple returns an already-applied receipt. Any command,
edge, child identity, or revision mismatch prevents publication and revision
reassignment. The fence enters `ReconciliationRequired` and fails closed with
`session_tree_reconciliation_required` until dedicated reconciliation proves the
child link/index and pending command. Reserved and rejected reservations never
appear in `mo session tree`.

Fence phases answer commands as follows:

- **Reserved reservations:** `Reserve` is allowed. `BeginFinalize` is allowed
  and one command receives the next revision. `BeginStopSnapshot` rejects
  affected reservations before materializing membership. `BeginDetach` is
  allowed and one command receives the next revision.
- **AttachAwaitingAck:** `Reserve` is allowed but remains invisible.
  `BeginFinalize` replays the same command; other commands receive
  `finalize_busy`. `BeginStopSnapshot` and `BeginDetach` fail with
  `session_tree_mutation_pending` until attachment recovery publishes.
- **DetachAwaitingAck:** `Reserve` is allowed but remains invisible.
  `BeginFinalize` and `BeginStopSnapshot` fail with
  `session_tree_mutation_pending`. `BeginDetach` replays the same command;
  other commands receive `detach_in_progress`.
- **Snapshot materializing:** `Reserve` does not create a reservation and the
  request fence remains `validation-pending`. `BeginFinalize` and
  `BeginDetach` are retryable without changing their plans.
  `BeginStopSnapshot` replays the same operation; another stop is busy.
- **Published nonterminal stop with the parent in frozen membership:**
  `Reserve` returns `parent_tree_stop_in_progress` without creating a
  reservation. `BeginFinalize` rejects and aborts the existing plan or
  reservation. `BeginStopSnapshot` replays the operation; another stop is busy.
  `BeginDetach` is allowed and does not change frozen targets.
- **Published nonterminal stop outside frozen membership:** `Reserve`,
  `BeginFinalize`, and `BeginDetach` are allowed. `BeginStopSnapshot` replays
  the existing operation; another stop is busy.
- **ReconciliationRequired:** all four commands are rejected.

Snapshot materializing is only a short fence phase for creating an authoritative
snapshot. It has no executable targets. A published stop constrains its frozen
membership and does not impose a Project-wide cap on unrelated trees. A
revision-assigned mutation must recover through publication before another
mutation or stop snapshot can assign or publish a revision. Invisible
reservations are not subject to this ordering.

Attach and detach recover through four windows: after pending but before the
child transition, after the child receipt but before fence acknowledgement,
after acknowledgement but before `Commit`, and after publication but before the
caller receives the result. Each window replays only the same tuple. Attach's
last window remains subject to its attached-reservation submission gate. Detach's
last window replays the published historical result.

### Tree reads

A tree read pins a topology snapshot at the current Project `GraphRevision`.
For each breadth-first frontier, it batch-reads raw child candidates by
`(ProjectId, ParentSessionId)` before applying the attachment predicate. SQL
must not silently hide malformed candidates by applying the final predicate
first.

At revision `R`, a candidate claiming to be attached must have all of these
facts:

- a child-row identity in the same Project;
- non-empty parent, edge, and child launch Job identities;
- `AttachedRevision > 0` and `AttachedRevision <= R`;
- `DetachedRevision` null or greater than both `R` and the attached revision;
- no self edge, duplicate child, duplicate edge, or cycle.

Only a row proven invisible at `R` by consistent detach history may be skipped.
If a candidate selected from a reached parent is inconsistent, the read returns
`session_tree_projection_inconsistent` with no partial tree. A stop snapshot
source cannot persist membership or targets and moves the materializing fence to
`ReconciliationRequired`. Unreachable bad rows do not require a Project-wide
scan.

Validated edges are traversed recursively and batch-joined with current Session
summaries. The read does not activate a Session grain for each node or scan
unrelated Sessions.

Return order is breadth-first. Siblings sort by `(AttachedRevision, EdgeId)`.
Recursive traversal builds each node's ancestor path from that sort key and
sorts by `(depth, ancestor path)`. The first page's opaque cursor pins Project,
root, revision, and the final `(depth, ancestor path)`. Later pages replay the
same topology snapshot from that cursor. A cursor chain therefore cannot
silently duplicate or omit nodes. Concurrent attachment or detach appears only
in a new query without a cursor. An invalid or mismatched cursor is rejected
instead of switching to the latest revision.

Page and continuation limits bound one diagnostic read. They do not define or
reject business tree depth, width, or total node count.

### Operational bounds

The tree has no business-level depth, width, or attached-node admission cap.
Normal Agent `MaxConcurrentRuns`, launch queue capacity, Session input
capacity, Runner capacity, and storage retention still apply. A child launch
queues as an ordinary AgentJob when its target Agent is busy and uses ordinary
visible launch backpressure when capacity is insufficient. The tree creates no
separate scheduler and never rejects spawn for a structural count.

## Spawn

### Invocation surface

The canonical CLI command is:

```bash
mo agent spawn <agent-ref> --project <project-id> --parent-session <session-id> \
  --prompt "<brief>" --idempotency-key <key>
```

`--parent-session` is the explicit caller identity. The CLI cannot infer the
parent from the current directory, Runtime Session, process environment, or a
previous launch. `--idempotency-key` is required; after network failure the
CLI retries with the same key. The child inherits the parent's Workspace and
WorkDir.

The canonical Server surface is:

```text literal
POST /api/projects/{projectRef}/agent-sessions/{parentSessionId}/spawns
Idempotency-Key: {key}

{ "targetAgentRef": "reviewer", "prompt": "..." }
```

The path `parentSessionId` and idempotency header define the caller and replay
boundary. The body accepts no `workDir`, `workspacePath`, Runner, Runtime,
Instructions, Model, Skills, Workspace override, or arbitrary filesystem path.
Within `(ProjectId, ParentSessionId)`, one key expresses one canonical request.
A replay with a different prompt, target, or caller returns HTTP 409
idempotency conflict.

The child directory comes only from the parent's authoritative Workspace
binding. Git worktree is a Git-domain tool selected by the Agent; it is not a
spawn or Session isolation primitive.

`parentSessionId` identifies delegation authority, not a new bearer credential.
Existing caller authentication first decides whether the caller can operate the
Project. Server then validates delegation against the Session's persisted launch
snapshot. The first release creates and propagates no per-Session process
credential.

On first acceptance, Server resolves `targetAgentRef` to a stable Agent ID. An
old name is invalid after rename; the Agent must discover the current name or
use the stable ID. The response reuses ordinary launch references for the child
AgentJob, AgentSession, first Input, first Turn, and observation. It also
returns `parentSessionId` and `edgeId`.

### Acceptance conditions

Before writing a coordinator plan, Server must confirm all of the following:

1. `parentSessionId` belongs to the requested Project and carries an immutable
   Agent execution definition, capability snapshot, and canonical AgentId.
   Direct launches and Agent Connection Mohist AgentSessions can satisfy this
   condition. Workflow inline Sessions cannot.
2. The parent snapshot contains the resolved target's stable Agent ID.
3. The target is currently active, and its launch definition and Agent Readiness
   resolve normally.
4. Ordinary Agent launch readiness and queue admission allow a new child. The
   target's `MaxConcurrentRuns` may still queue it afterward.
5. The parent has a currently usable authoritative WorkDir. The spawn body,
   Agent Connection conversation, caller process path, and another Session's
   directory cannot provide, replace, or upgrade it.
6. The parent has a current, confirmed, usable Runtime binding containing
   RunnerId, Runtime, and runtimeSessionId. Spawn is prohibited when Activity
   is `unknown`, the binding is missing, the Runner no longer exists, or the
   binding disagrees with Session WorkDir.
7. No attached ancestor containing the parent belongs to a cascade stop
   operation without a terminal outcome.

Conditions 5 and 6 define first-release shared-WorkDir behavior. The child
inherits the parent's persisted WorkDir and is pinned to the RunnerId in the
parent binding. Copying only the path and scheduling on any eligible Runner is
invalid because the directory may exist only on the parent's Runner.

The parent source does not bypass these checks. An Agent Connection parent with
both facts may spawn. A parent without either fact is rejected under condition
5 or 6. Slack conversation, caller path, and another Session directory cannot
fill a missing fact.

When facts cannot be confirmed, Server rejects or retains the request as
follows:

- No parent WorkDir: terminal pre-plan result `parent_workdir_unavailable`.
  No child is created.
- Missing, unknown, stale, or unusable parent binding: result
  `parent_runner_binding_unavailable`. The request fence remains
  `validation-pending`, no child is created, and same-key retry revalidates it.
- Target absent from the snapshot: terminal `subagent_not_allowed`.
- Target archived: terminal `target_agent_archived`.
- Target Agent Readiness is `needs-setup`: terminal `agent_needs_setup`.
- Parent belongs to nonterminal cascade-stop membership: result
  `parent_tree_stop_in_progress`. The request fence remains
  `validation-pending`, no child is created, and same-key retry revalidates it.

An offline Runner does not authorize switching Runner while the binding remains
current. The result remains `parent_runner_binding_unavailable` on a
`validation-pending` fence. Recovery retries the same key and never sends the
child to another Runner.

### Coordinator, atomicity, and recovery

Spawn extends the existing `AgentLaunchCoordinator` keyed by idempotency. It
does not add a `SubagentLauncher` or a second Job pipeline. The coordinator
scope includes `parentSessionId`.

It first persists a `SpawnRequestFence` without child identity:

```text literal
SpawnRequestFence
  ProjectId
  ParentSessionId
  IdempotencyKey
  RequestFingerprint
  Outcome: validation-pending | preplan-rejected | admitted
  PreplanRejectionReason?
```

This fence is the authority for
`(ProjectId, ParentSessionId, IdempotencyKey)`. It freezes caller, key, and
fingerprint. It creates or reserves no Job, Session, Input, Turn, edge, or
reservation identity.

`validation-pending` is retryable. Same-fingerprint replay revalidates current
facts and can advance the same request to an admitted plan. Temporary binding
unavailability, `unknown` Agent Readiness, other temporarily unconfirmed launch
readiness, and `parent_tree_stop_in_progress` remain pending and cannot freeze
the key as rejected.

Only definite canonical or authorization invalidity becomes
`preplan-rejected`: the caller is outside the Project or is not a delegating
Mohist AgentSession; the parent has no authoritative WorkDir; the target ref
cannot resolve to an Agent ID in the immutable snapshot; the target is absent
from that snapshot or archived; or target Agent Readiness is `needs-setup`.
Same-fingerprint replay returns that terminal result. A different fingerprint
returns HTTP 409 idempotency conflict.

Only after validation succeeds does the coordinator persist a launch plan with
child identities. The plan includes:

- `SpawnOrigin`: parentSessionId, parent Agent ID, edgeId, and caller key;
- parent WorkDir, pinned RunnerId, and complete expected parent binding;
- target stable ID, presentation snapshot, and child execution definition;
- the Job, Session, Input, and Turn identities used by ordinary launch.

After planning, the coordinator does not reread the mutable target Agent or
parent capability snapshot. It extends `PrepareJob -> EnsureInitialLaunch ->
SubmitJob` with reservation, final check, and abort:

1. Persist the request fence with target and exact prompt fingerprint.
2. Run pre-plan validation. Keep `validation-pending`, terminally reject with
   no child artifacts, or persist the launch plan and reserve EdgeId at the
   `SessionTreeMutationFence`.
3. Prepare the child AgentJob with pinned RunnerId and child WorkDir.
4. Create the child AgentSession with immutable WorkDir, its initial Input, and
   its initial Turn.
5. Final-check reservation, parent WorkDir, binding, and stop admission.
6. Finalize the child-owned `SessionParentLink` through the fence protocol.
7. Submit the same prepared AgentJob to its pinned Runner.

If pre-plan validation observes temporary unavailability, the request remains
`validation-pending` with no plan, reservation, or child identity. It creates no
Job, Session, Input, Turn, or link. Same-key retry repeats validation until
admitted or terminally rejected. A terminal rejection creates no child and
same-key replay returns only that fixed result. A new key expresses new
delegation.

A persisted plan contains immutable child identities, expected WorkDir and
binding, and `LinkReservation`. Same-key replay recovers only its original
result. It cannot choose another parent, target, WorkDir, or Runner. Every later
command has a stable command identity and repeated execution returns an
already-applied acknowledgement. Coordinator reminders recover from this
fence.

A final-check or abort rejection after planning is durable and terminal. Even if
the parent later recovers, same-key replay returns the plan's original result. A
new key is required for later delegation.

Before final attachment, Job, Session, Input, and Turn are provisional. They are
not visible through `mo session tree`, the normal spawn success response, or
ordinary Session commands. The initial Turn may be queued and Activity may be
active while the coordinator recovers, but this never means work was submitted
to Runner.

Finalizing attachment compares expected WorkDir, binding, and stop admission
inside the mutation fence. The child writes the attached link and index at the
assigned revision and returns the exact receipt. Only after acknowledgement is
validated and `Commit` completes does the child become visible and the success
response expose it.

`SubmitPreparedLaunch` carries the plan's attached reservation. Every submit and
recovery path validates it. Without an attached reservation, or after a
rejection fence, the result is always `must-not-submit`, even if the Job is
pending.

If final check finds a parent reset, changed WorkDir or binding, reservation
rejection by a stop snapshot with `parent_tree_stop_in_progress`, or another
unrecoverable conflict, the coordinator first persists the plan as rejected and
then runs a stable abort command:

- reservation becomes `rejected`;
- prepared Job becomes terminal `cancelled` with reason `parent_link_rejected`;
- initial Turn becomes terminal `cancelled`;
- Session Activity returns to `idle`, while AgentSession remains nonterminal.

A written initial Input remains only as audit of the rejected plan. It is
invisible to ordinary Session input, tree reads, and launch success. No
`SessionParentLink` exists, so this cancellation does not create a terminal
report or append an Input to the parent.

Abort may be interrupted after any participant call. Replaying the same abort
command completes remaining participants and preserves the rejected and
cancelled results. If a participant was never created, it remains absent. The
replay exposes no ordinary successful Job or Session reference for an
unaccepted child. A plan whose attachment already finalized is not changed by
later target rename, archival, or parent configuration edits.

The child Job dispatch envelope carries `PinnedRunnerId`. AgentJob admission may
claim only that Runner. If it is unavailable, the Job retains ordinary
pending/retry state and cannot migrate through Project-wide Runner selection.

## Startup-known context

For every Agent launch, Server creates an immutable `AgentSessionStartup`,
persists it with the execution definition, and supplies it as Runtime-supported
startup or system context before the first dispatch. It differs from the
user-provided read-only `AgentStartupContext` and cannot borrow that context's
external-discussion semantics.

```text literal
AgentSessionStartup
  ProjectId
  SessionId
  ParentSessionId?
  AllowedSubagents: [{ AgentId, NameAtLaunch, DescriptionAtLaunch }]
  SpawnCommand:
    mo agent spawn <agent-ref> --project <project-id>
      --parent-session <session-id> --prompt "<brief>" --idempotency-key <key>
```

This Server-originated context is visible before the task prompt. It states that
the Agent decides target selection, brief generation, unique-key generation,
waiting, retries, and acceptance. The child also receives ParentSessionId and
can request help through ordinary
`mo session followup <parent-session-id>`.

Do not set `MOHIST_SESSION_ID` or any per-Session process environment variable.
If a Runtime has no system-context channel, Runner can receive only a startup
block explicitly marked by Server. It still cannot infer identity from the
environment, WorkDir, or Session files. Initial-launch Session settings are the
sole snapshot source for restart and follow-up.

## Messages and child terminal report

### Ordinary cross-session input

Parent-to-child steering and child-to-parent help requests both use ordinary
`mo session followup` through `SessionInput` and `AgentTurn`. The tree provides
only discoverable Session IDs and startup context. It invents no message format,
separate queue, or Runtime protocol.

### Authoritative terminal trigger

AgentSession never becomes terminal. A child delegation becomes terminal when
its spawned `ChildLaunchJobId` enters `completed`, `failed`, or `cancelled`.
`unknown` is not terminal. A terminal initial or later AgentTurn is not a report
trigger.

In that Job transition, AgentJob persists a terminal event with SpawnOrigin. It
carries child Job, Session, status, initial Turn, result-observation reference,
and EdgeId. It does not write the parent or copy transcript, output, or natural-
language result into the parent.

The event handler claims the report on the child-owned link, making
`TerminalReport` the sole delivery obligation state.

### Durable idempotent delivery

The at-least-once handler uses this order:

1. The AgentJob terminal event arrives.
2. The child Session atomically claims the report on its attached link.
3. The handler appends a normal parent SessionInput.
4. The parent accepts or reuses its AgentTurn through normal rules.
5. The child link records the delivered parent InputId.

Claim and `attached -> detached` compete in one child Session transaction:

- detach first: claim becomes `suppressed`, and no report is generated;
- claim first: report becomes `pending`, and later detach does not revoke it;
- detach never deletes a delivered parent Input.

The parent Input uses idempotency key
`subagent-terminal:{edgeId}:{childLaunchJobId}`, source
`subagent-terminal`, and structured provenance containing child Session, Job,
Turn, and result references. Its visible body states only the child, terminal
state, and result location. Duplicate events, activation loss, and handler retry
therefore create at most one logical Input. Same-key replay returns the original
Input and Turn.

When the parent is idle, the Input starts a new Turn. When it is active, the
Input enters the current or a later Turn in normal order. If input capacity,
unknown Activity, or temporary unreachability blocks delivery, the link remains
`pending`. Server retries with the same key after capacity or Activity changes
and during dispatcher recovery. It never discards the report or rolls the
terminal child Job back to incomplete. Report delivery and child Job result are
separate durable facts.

## Cascade stop and detach

Stop, detach, and spawn can race. The Session owner arbitrates them:

- a stop that lands before spawn completes cancels the spawn;
- a detach that lands first removes the subtree from that stop's scope.

### Cascade stop

`mo session stop <session-id> --idempotency-key <key>` creates a
`SessionTreeStopOperation`. It does not mark any AgentSession as stopped. The
public command accepts only Project, root Session, and idempotency key. Server
derives operation ID and request fingerprint. The caller cannot provide graph
revision, membership, or targets.

Same-key retry recovers the same operation. The operation is readable by root
Session and idempotency key. Only a new key represents a new stop request.

Server selects revision `R` through a fence command ordered after earlier fence
mutations. It persists the materializing operation identity, then invokes a
stateless revision-pinned snapshot source. The source reads child-owned links
where `AttachedRevision <= R` and `DetachedRevision` is null or greater than
`R`. It generates deterministic breadth-first membership and reads each
member's current durable Turn, binding, and stable child stop-operation ID.

The source stores no topology and accepts no client- or Runner-submitted member,
Turn, or binding. It is not a second topology authority. Only after the fence
persists root, membership, `R`, and source targets does it publish the stop
snapshot.

Same-command replay during materialization recovers the same operation and `R`
and drives the unfinished source read again. Facts not yet persisted are not an
accepted snapshot. After publication, replay returns persisted membership and
targets without traversing the tree or selecting bindings again.

The fence orders races as follows:

- Detach linearized before snapshot: its subtree is outside the snapshot.
- Attachment finalized before snapshot: the child is in the snapshot and
  ordinary target rules handle its current work.
- Snapshot linearized before detach: the frozen subtree remains in scope.
- An earlier unfinalized reservation whose parent is in membership is rejected
  and excluded. Its coordinator may abort but never submit.
- A new spawn, reservation, or attachment inside published stop membership
  returns `parent_tree_stop_in_progress` on a `validation-pending` request fence.
  An existing plan or reservation may abort but never submit.

An attachment or detach mutation must recover through publication before stop
materialization begins. Stop retry uses persisted targets and does not reread
the changing tree.

If a snapshot includes an attached child not yet submitted, its target
sub-operation cancels that queued work by initial Job and Turn identity and
writes cancellation into the plan's submission gate. Coordinator recovery can
never submit afterward. The link was accepted, so the Job's terminal cancelled
result still follows normal terminal-report rules. This differs from abort after
reservation rejection, which has no callback.

Each target uses existing Server turn-control semantics:

- no nonterminal Turn: `already-idle`; Session continues to exist;
- queued Turn: `cancelled`; Runner is not contacted;
- executing Turn: request stop from the expected-binding Runner and record
  `stop-requested`;
- Runner did not receive the request: `pending`; retry the same sub-operation;
- Runner may have acted but the result is unconfirmed: `unknown`; never invent
  idle or cancellation;
- target replaced binding or Turn: `rejected`; never stop later work.

Operation summary derives only from target facts. All determinate targets produce
`completed`. Determinate success mixed with `rejected` produces `partial`. Any
unconfirmed result produces `unknown`. Remaining safe delivery produces
`running`. Only `completed` and `partial` are terminal. `running` and `unknown`
retain the stop-admission fence. Unknown is not safe completion and must retry or
reconcile the original operation.

While stop is nonterminal, new spawns in membership remain rejected with
`parent_tree_stop_in_progress`. Retry checks or resends the same persistent
sub-operation for the original target. It does not traverse the tree, create a
child, replay input, or stop a later Turn outside the snapshot.

Cascade stop ends current work in the snapshot but never closes a Session.
Parent and child Sessions can continue later through an explicit follow-up,
which creates a new Turn outside the old operation.

### Detach

`mo session detach <child-session-id>` requires an attached child. It changes
only the child Session link to `detached` as a fence mutation. The fence first
persists the full detach tuple and assigned revision. The child idempotently
writes the detach revision and index receipt with the same command, edge,
parent and child identity, child launch Job, expected attached revision, and
assigned revision. The fence accepts only that exact receipt and commits with
the same command, edge, and revision.

Detach returns removed parent and edge identity. It does not cancel the child,
move WorkDir, change Source, or convert the child into a new Agent. The detached
child is a new tree root and attached descendants retain their structure.

Detach is an idempotent target state. Retrying an already detached child returns
the historical link. It cannot reparent. A wrong revision or receipt mismatch
cannot rewrite history or advance the graph. It enters `ReconciliationRequired`
instead of trying another revision.

Terminal-report delivery is the sole authority for the race between detach and
a child report.

`PendingDetach` is the durable production-recovery work record. There is no
separate detach-operation grain. Each mutation fence has one fixed periodic
recovery reminder as an at-least-once wake-up. `BeginDetach` is accepted only
after the reminder is registered or updated successfully. The reminder is not
cancelled when there is no pending work or after commit. It no-ops with no
pending work.

Fence activation also ensures the reminder is registered, so activation loss
cannot leave detach without a wake-up. The reminder drives
`ApplyDetach(exact tuple)`, persists the exact acknowledgement, then commits the
same command, edge, and revision. Temporary unreachability retains pending
state and retries the tuple. A mismatched receipt enters reconciliation and
cannot use a new revision.

Recovery windows are deterministic: call the child after Begin is written but
before the child call; call it again for an already-applied receipt; commit only
after acknowledgement; and return the historical result after commit. A
post-commit reminder no-ops. Replay cannot call the child again or advance the
graph.

## Scheduled input interaction

Scheduled input is owned by the target AgentSession and is authoritative in
[`scheduled-input.md`](scheduled-input.md). A parent may create a schedule for
a child through the ordinary Session API, but the tree link owns neither the
schedule nor its delivery identity.

- **Cascade stop:** a schedule is not in the frozen stop snapshot and is never
  deleted or cancelled by it. Delivery waits while stop is nonterminal and may
  create a later Turn outside that snapshot.
- **Detach:** changing `SessionParentLink` does not change a schedule's target
  Session. A detached Session still receives scheduled Input when due.
- **Spawn:** a schedule addresses an existing Session. It never launches a child
  when due.

## Non-goals

- Do not introduce an Agent resource hierarchy, temporary identity-free Agent,
  or cross-Project spawn.
- Do not treat AgentSession as completed, failed, stopped, or closed.
- Do not create fork-join, automatic retry, automatic summarization, Agent
  recommendation, task planning, acceptance, or parent-agent patrol mechanisms.
- Do not copy transcript, output, tool calls, or Runtime context into the parent.
  A terminal report carries references only.
- Do not accept `--work-dir`, `workspacePath`, a named Workspace override, or any
  filesystem path input. The child directory comes only from the inherited
  parent Workspace ([`workspaces.md`](workspaces.md)).
- Do not let Runner infer parent or child identity from WorkDir, environment
  variables, or Runtime Session, or select a child Runner different from the
  parent binding.
- Do not claim that the tree replaces Issue, Workflow, Project, Workspace, or
  Materialization ownership and isolation models.
