---
status: implemented
---

# Subagent and Session Tree Design

A session tree is an optional parent-child relationship between AgentSessions. It lets a running
Mohist Agent delegate a new Agent launch at runtime. It does not create an Agent hierarchy, a new
message type, a Session terminal state, or a workflow orchestrator.

See [`../docs/subagents.md`](../docs/subagents.md) for product behavior. The base lifecycles of
AgentJob, AgentSession, SessionInput, and AgentTurn remain defined in
[`agent-execution.md`](agent-execution.md); this document defines only how they compose in one
spawn.

```text diagram
Child AgentSession
  | owns
  v
SessionParentLink -- references --> Parent AgentSession
  ^
  | orders publication
SessionTreeMutationFence (Project-scoped)
  |
  +-- freezes membership --> SessionTreeStopOperation snapshot
```

The child-owned link is the topology truth, which avoids a second mutable child list on the parent.
The Project fence orders link publication and freezes cascade-stop membership, but it never becomes
another topology owner.

Scheduled input is an ordinary AgentSession capability, not a tree capability. Its design is
authoritative in [`scheduled-input.md`](scheduled-input.md); this document records only the points
where a schedule meets spawn, stop, or detach.

## Boundaries

- Agent resources remain flat within a Project. Subagent is only the role of a child AgentSession
  in a parent-child relationship.
- Child delegation remains an ordinary Agent launch: one `AgentJob`, one `AgentSession`, the first
  `SessionInput`, and the first `AgentTurn`. There is no second launch or Runner dispatch pipeline.
- `AgentSession.Source` explains why the session was created and is immutable. The parent-child
  relationship is a separate `SessionParentLink`, not part of Source; detach cannot rewrite Source.
- The Server resolves capability, identity, working directory, and Runner and persists the link,
  operations, and message acceptance. The Runner executes only resolved and pinned child work.
- `SessionInput` and `AgentTurn` are the only message model between parent and child. Terminal
  reports, steering, and requests for help cannot add an inbox, message aggregate, or transcript
  branch.
- The session tree provides spawn, inspect, notification, stop, and detach primitives. Fork-join,
  wait policy, retries, task recommendations, task decomposition, and acceptance remain decisions
  made by the Agent's own Instructions and Skills.

## Model

### Capability declaration and launch snapshot

An Agent definition stores `AllowedSubagentAgentIds`, an ordered set of stable Agent IDs in the
same Project. It does not copy the target Agent's Instructions, Runtime, Skills, or concurrency
configuration.

Every Agent launch resolves that declaration into an immutable `AllowedSubagentSnapshot`:

```text literal
AllowedSubagentSnapshot
  AgentId
  NameAtLaunch
  DescriptionAtLaunch
```

The snapshot is part of the parent AgentJob execution definition and is written to the settings of
the AgentSession it creates. Follow-ups in the same Session do not resolve the capability
declaration again. The Server places each Session's own snapshot in its startup context; it is not
an environment variable or temporary client input.

Name and state rules are:

| Situation | Rule |
|---|---|
| Configuration declaration | Store only the target's stable ID; the target must belong to the same Project. |
| Target renamed after parent launch | The parent snapshot retains the old name/description. At spawn, a current name or ID that resolves to the same Agent ID remains authorized. |
| Target archived after parent launch | An unaccepted spawn is rejected with terminal pre-plan result `target_agent_archived`. A child already accepted by the launch coordinator is not revoked by later archival. |
| Target restored to active | New spawns may again use the declared ID; an existing snapshot needs no change. |
| Self-spawn | Allowed only when this Agent's stable ID is explicitly declared; it follows ordinary launch scheduling like every other target. |
| Cross-Project | Always rejected. The parent Session, target Agent, child Job, and child Session must belong to the same Project. |

Archiving an Agent does not automatically remove its ID from configuration. The declaration
therefore retains deterministic meaning if the Agent becomes active again, but archival never
permits a new child launch. Agent deletion is outside this design.

### SessionParentLink

A child AgentSession owns an optional `SessionParentLink`. The child is the sole write authority
for the link because only it can gain or lose its one parent session. The parent aggregate does not
store a mutable child list.

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

`ChildLaunchJobId` is the initial AgentJob for this delegation, not a terminal marker for the child
Session. The link is established during the child's initial launch and can only transition
`attached -> detached`. Attaching an existing Session, reparenting, and reattaching are unsupported.

These restrictions guarantee directly that:

- a child has at most one current parent;
- a newly created child has no ancestor before its link is established, and the link never changes
  target, so no cycle can form;
- after detach, the child and its still-attached descendants form another tree while Source,
  workDir, Runtime binding, transcript, and AgentJob remain unchanged;
- the historical link retains ParentSessionId, EdgeId, DetachedAt, DetachedRevision, and the
  delivered parent InputId for audit. Default tree queries return only `attached` edges.

### Tree mutation fence and graph revision

Each Project has one durable `SessionTreeMutationFence`. It is the sole linearization point for
attachment, detach, and cascade-stop snapshots and maintains a strictly increasing
`GraphRevision`. It is not a second tree model: the child-owned `SessionParentLink` remains the
edge authority. The fence holds only `LinkReservation` values for unfinished plans, pending
mutation commands, and the short transaction needed for one mutation.

```text literal
LinkReservation
  EdgeId
  ParentSessionId
  ChildSessionId
  State: reserved | attached | rejected
  RejectionReason?
```

A reservation does not create a visible tree edge. Multiple `reserved` reservations for different
edges can coexist. Reservation neither changes `GraphRevision` nor counts as an in-flight topology
mutation. Only revision-assigned `AttachAwaitingAck` or `DetachAwaitingAck`, including a received
participant receipt not yet published, is an in-flight mutation. Finalizing attachment revalidates
the parent's authoritative workDir, expected binding, and stop admission under the same fence.

A standalone `ReadBinding` is observation only and cannot authorize attachment. The parent
`AgentSession` is the binding authority. It owns `CurrentBinding`, an internal `BindingEpoch` that
increments on every establishment or replacement, and a durable `BindingUseReceipt`. The fence,
child, and Runner cannot write or issue a receipt. Before `BeginFinalize`, the coordinator calls
the parent's `AcquireChildAttachBinding` with the complete expected binding, epoch, workDir,
command, and edge. The parent compares those facts and writes the held receipt in one transaction.
Reset and every binding replacement also compare expected binding/epoch and reject replacement
with `binding_attach_in_progress` while a held receipt exists. If Reset linearizes first, acquire
returns mismatch and the plan rejects/aborts with `parent_binding_changed`. If acquire linearizes
first, reset cannot replace the binding.

The complete pending tuple and participant receipt for attach contain Project, command ID, edge,
parent/child Session, child launch Job, parent workDir, RunnerId, Runtime, runtimeSessionId,
BindingEpoch, BindingUseReceipt ID, expected link state `absent`, and assigned revision. The detach
tuple instead contains command ID, edge, parent/child Session, child launch Job, expected attached
revision, and assigned revision. Order is fixed:
`Acquire -> BeginFinalize -> child exact attach -> acknowledgement -> Commit -> Release`.
`BeginFinalize` revalidates the held receipt, reservation, and stop admission before assigning a
revision. The child writes its link/index and exact receipt in one child transaction. The fence
records acknowledgement only when the receipt matches field by field, then `Commit`s with the same
command, edge, and revision. The parent may idempotently `Release` its receipt only after publish or
durable abort. A reset after publish is subsequent work and cannot retroactively revoke an attached
plan. Replaying the same acquire command returns the original receipt; coordinator activation
recovery replays this order from the held receipt. Receipts do not expire by time. A receipt/child
mutation mismatch stays held until reconciliation instead of releasing prematurely.

A child replay of the same tuple returns an already-applied receipt. Any command, edge, child
identity, or revision mismatch prevents publish and revision reassignment. The fence enters
`ReconciliationRequired` and fails closed with `session_tree_reconciliation_required` until a
dedicated reconciliation proves the state of the child link/index and pending command. The child
therefore remains the sole link write authority, and tree reads never expose a half-finished
cross-store mutation. Reserved/rejected reservations never appear in `mo session tree`.

| fence phase | `Reserve` | `BeginFinalize` | `BeginStopSnapshot` | `BeginDetach` |
|---|---|---|---|---|
| One or more `Reserved` | allow | allow; one command gets the next revision | allow; reject affected reservations before materializing the snapshot | allow; one command gets the next revision |
| `AttachAwaitingAck` | allow; a new reservation remains invisible | replay same command; other commands get `finalize_busy` | `session_tree_mutation_pending`; recover and publish attach first | `session_tree_mutation_pending` |
| `DetachAwaitingAck` | allow; a new reservation remains invisible | `session_tree_mutation_pending` | `session_tree_mutation_pending`; recover and publish detach first | replay same command; other commands get `detach_in_progress` |
| Snapshot materializing | do not create a reservation; request fence remains `validation-pending` | retryable; do not change the plan | replay same command; other stop is busy | retryable |
| Published nonterminal stop; parent is in frozen membership | do not create a reservation; `parent_tree_stop_in_progress` | reject and abort existing plan/reservation | replay same operation; other stop is busy | allow; do not change frozen targets |
| Published nonterminal stop; parent is outside frozen membership | allow | allow | replay same operation; other stop is busy | allow |
| `ReconciliationRequired` | reject | reject | reject | reject |

Snapshot materializing is only a short fence phase used to create an authoritative snapshot; it
does not yet have executable targets. A published stop constrains only its frozen membership and
does not impose a Project-wide structural cap on unrelated trees. A revision-assigned mutation
must recover through publish before another mutation or stop snapshot can assign or publish a
revision. Invisible reservations are not subject to this ordering.

Attach and detach have the same four recovery windows: after pending but before the child
transition; after the child writes its receipt but before fence acknowledgement; after
acknowledgement but before `Commit`; and after publish but before the caller completes its next
step. Each window replays only the same tuple. For attach, the last window remains subject to the
attached-reservation submission gate. For detach, it only replays the published historical result.

A tree read first pins a topology snapshot at the current Project `GraphRevision`. For every
breadth-first frontier it batch-reads raw child candidates by `(ProjectId, ParentSessionId)` before
determining whether each edge exists at revision `R`. SQL cannot silently filter malformed
candidates with the final attachment predicate first. A candidate claiming to be attached at `R`
must have a child-row identity in the same Project; non-empty parent, edge, and child launch Job;
an `AttachedRevision` greater than zero and no greater than `R`; and a `DetachedRevision` that is
either null or greater than both `R` and the attached revision. It cannot be a self edge, duplicate
child/edge, or cycle. Only a row proven invisible at `R` by consistent detach history may be skipped.

If any candidate selected from a reached parent is inconsistent, the tree read returns
`session_tree_projection_inconsistent` without a partial tree. A stop snapshot source cannot
persist membership or targets and moves the materializing fence to `ReconciliationRequired`.
Unrelated bad rows unreachable from the root at `R` do not require a Project-wide scan. Validated
edges are then traversed recursively and batch-joined with current Session summaries. The read does
not activate a Session grain per node or scan unrelated Sessions in the Project.

Return order is fixed as breadth-first. Sibling order at each level is
`(AttachedRevision, EdgeId)`. Recursive traversal builds the ancestor path of that sort key for
each node and finally sorts by `(depth, ancestor path)`. The first page's opaque cursor pins
Project, root, revision, and the final `(depth, ancestor path)`. Later pages replay the same
topology snapshot from that cursor. One cursor chain therefore neither duplicates nor omits nodes;
concurrent detach or attachment changes appear only in a new query without a cursor. A cursor that
is invalid or mismatches Project/root is rejected rather than silently switching to the latest
revision. Page/continuation limits one diagnostic read; it neither defines nor rejects business
tree depth, width, or total node count.

### Operational bounds

The session tree has no business-level depth, width, or attached-node admission cap. Normal Agent
`MaxConcurrentRuns`, launch queue capacity, Session input capacity, Runner capacity, and storage
retention policy still apply. A child launch queues as an ordinary AgentJob when the target Agent
is busy and uses ordinary visible launch backpressure when capacity is insufficient. The tree
relationship creates no separate resource scheduler and does not reject spawn based on a
structural count.

## Spawn

### Invocation surface

The canonical CLI command is:

```bash
mo agent spawn <agent-ref> --project <project-id> --parent-session <session-id> \
  --prompt "<brief>" --idempotency-key <key>
```

`--parent-session` is the explicit parent target, not caller identity. The
trusted command envelope derives canonical `callerId` from the authenticated
principal or adapter identity; the CLI cannot infer either the parent target or
caller from the current directory, Runtime Session, process environment, or most
recent launch.
`--idempotency-key` is the required invocation identity; after network failure the CLI retries
with the same key. The child always inherits the parent's Workspace and working directory.

The canonical Server surface is:

```text literal
POST /api/projects/{projectRef}/agent-sessions/{parentSessionId}/spawns
Idempotency-Key: {key}

{ "targetAgentRef": "reviewer", "prompt": "..." }
```

The path `parentSessionId` and idempotency header identify the target and caller
key, while the trusted envelope supplies `callerId`. Together they form
`AdmissionScope(callerId, projectId, subagent-spawn, parentSessionId,
idempotencyKey)`. The body accepts no `workDir`, `workspacePath`, Runner,
Runtime, Instructions, Model, Skills, Workspace override, or arbitrary
filesystem path. One key can express only one canonical caller intent in that
scope. A replay with a different prompt, target, descriptor, or caller returns
an HTTP 409 idempotency conflict.

The child directory comes only from the parent's Workspace binding
(`workDir = parent authoritative workDir`). The platform provides no Session-level isolation
primitive. Git worktree is a Git-domain tool whose use is decided by the Agent and does not enter
the spawn contract.

`parentSessionId` is the explicit identity of delegation authority, not a new bearer credential.
Existing caller authentication first decides whether the caller can operate the Project. The
Server then validates delegation of the target against the Session's persisted launch snapshot.
The first release neither creates nor propagates a new process credential for each Session.

On first acceptance, the Server resolves `targetAgentRef` to a stable Agent ID. After a target is
renamed, its old name is no longer a valid ref; the Agent should discover the current name or use
the stable ID. The response reuses ordinary launch references for `AgentJob`, `AgentSession`, the
first Input, the first Turn, and observation, and additionally returns parentSessionId and edgeId.

### Acceptance conditions

Before writing a coordinator plan, the Server must confirm all of the following:

1. parentSessionId belongs to the requested Project and carries an immutable Agent execution
   definition, capability snapshot, and canonical AgentId. Direct launches and Agent Connection
   Mohist Agent Sessions can satisfy this condition; Workflow inline Sessions cannot.
2. The parent Session capability snapshot contains the resolved target's stable Agent ID.
3. The target is currently active, and its launch definition and readiness resolve normally.
4. Ordinary Agent launch readiness and queue acceptance allow a new child. The target's
   `MaxConcurrentRuns` only determines whether it subsequently queues.
5. The parent Session has a currently usable authoritative `WorkDir`. The spawn body, Agent
   Connection conversation, caller process path, and directory of another Session cannot provide,
   replace, or upgrade it.
6. The parent Session has a current, confirmed, usable Runtime binding containing `RunnerId`,
   runtime, and runtimeSessionId. Spawn is prohibited when activity is `unknown`, the binding is
   missing, the Runner no longer exists, or the binding disagrees with Session workDir.
7. No attached ancestor containing the parent belongs to a cascade stop operation that has not
   reached a terminal outcome.

Conditions 5 and 6 together define first-release shared-workdir behavior. The child workDir comes
from the parent AgentSession's persisted authoritative workDir, not a client path, and child
admission is pinned to the RunnerId of that parent binding. Copying only the path and scheduling
the ordinary AgentJob on any eligible Runner is invalid because the directory may exist only on
the parent's Runner.

The parent source does not change these checks. An Agent Connection parent with both facts may
spawn; one without them is rejected under condition 5 or 6. A Slack conversation, caller path, or
another Session's directory cannot fill a missing fact. The child inherits the authoritative
workDir from condition 5 and is pinned to the Runner in the parent binding.

When these facts cannot be confirmed, reject rather than fall back:

| Condition | Result |
|---|---|
| Parent has no workDir | Terminal pre-plan result `parent_workdir_unavailable`; create no child. |
| Parent binding is missing, unknown, stale, or has no usable Runner | `parent_runner_binding_unavailable`; keep the request fence `validation-pending`, create no child, and revalidate on same-key retry. |
| Target is absent from the snapshot | Terminal pre-plan result `subagent_not_allowed`; create no child. |
| Target is archived | Terminal pre-plan result `target_agent_archived`; create no child. |
| Target AgentReadiness is `NeedsSetup` | Terminal pre-plan result `agent_needs_setup`; create no child. |
| Target AgentReadiness is `Unknown`, or its required catalog cannot be read | Terminal pre-plan result `execution_catalog_unavailable`; create no child, Workspace, attachment, Job, Session, Input, Turn, or Runner effect. |
| Parent belongs to cascade-stop membership without a terminal outcome | `parent_tree_stop_in_progress`; keep the request fence `validation-pending`, create no child, and revalidate on same-key retry. |

An offline Runner does not authorize switching Runner while the binding remains current. The
result is still `parent_runner_binding_unavailable` and is recorded only on a
`validation-pending` request fence. After the Runner recovers, same-key retry revalidates the
binding and never sends the child to a different Runner.

### Coordinator, atomicity, and recovery

Spawn extends the existing `AgentLaunchCoordinator` persisted by idempotency key. It does not add
a `SubagentLauncher` or a second Job pipeline. The coordinator key includes parentSessionId in its
scope. It first persists a `SpawnRequestFence` without child identity:

```text literal
SpawnRequestFence
  Scope: AdmissionScope
    callerId
    projectId
    operationKind = subagent-spawn
    targetId = parentSessionId
    idempotencyKey
  ProjectId
  ParentSessionId
  CallerIntentFingerprint
  RequestFingerprint                 # caller intent + resolved child execution snapshot
  ChildExecution: ResolvedExecutionRead | null
  ResolutionFence: ExecutionResolutionFenceRead | null
  Outcome: validation-pending | preplan-rejected | admitted
  PreplanRejectionReason?
```

This fence is the canonical request authority for
`(callerId, ProjectId, ParentSessionId, IdempotencyKey)`. It always freezes
caller/key/caller-intent fingerprint and, when resolution succeeds, the final
child Runtime, Model, ReasoningEffort, Variant, `catalogVersion`, native mapping,
and source. It neither creates nor reserves Job, Session, Input, Turn, edge, or
reservation identity until it has an admitted plan. Parent Session identifies
the fence target; it never substitutes for the caller dimension.
`validation-pending` is a retryable observation before child acceptance: replay with matching
caller intent revalidates only the current parent binding or tree-fence facts and advances the same
request to an admitted plan when those conditions recover. If the fence already contains a child
execution snapshot, it reuses that snapshot and does not resolve a later Agent default or catalog.
`parent_runner_binding_unavailable` and
`parent_tree_stop_in_progress` may retain this outcome; they cannot freeze the
key as a rejection. Target `AgentReadiness.Unknown` and an unavailable target
catalog are instead terminal `execution_catalog_unavailable` pre-plan
rejections, so a new delegation never waits or creates child execution effects
while its execution semantics are unknown.

After authentication, authorization, a parseable keyed envelope, and closed
request validation, every definite pure target, execution, Workspace, or
attachment rejection advances it to the `preplan-rejected` tombstone: the
caller is not a delegating Mohist Agent Session; the parent has no authoritative
workDir; the target ref cannot resolve to an Agent ID in the parent's immutable
snapshot; the target is absent from that snapshot or archived; or target
AgentReadiness is `NeedsSetup`, for example because Instructions, Model, or
Runtime is invalid or missing; and target `AgentReadiness.Unknown` or an
unavailable target catalog. Same-key replay with matching caller intent always
returns this terminal pre-plan result; a different caller intent always returns
an HTTP 409 idempotency conflict. Missing credentials, failed authorization,
malformed envelopes, and missing/invalid keys occur before a fence and create no
tombstone.

Child execution uses the same `ResolveExecutionConfiguration` and durable
compare-and-fence boundary as Agent launch. Before the coordinator can admit a
new child plan, it resolves the target's saved tuple, records the resulting
execution snapshot and resolution fence, and atomically verifies that the target
Agent execution revision and selected catalog entry still match. A changed fence
retries pure resolution without child effects. The admitted plan therefore never
lets a Runner choose a later default, catalog entry, or native mapping.

Only after request-fence validation succeeds does the coordinator advance it to a launch plan
with child identities. The plan additionally persists:

- SpawnOrigin: parentSessionId, parent Agent ID, edgeId, and caller key;
- parent workDir, pinned RunnerId, and complete expected parent binding;
- target stable ID, presentation snapshot, and child execution definition;
- the Job, Session, Input, and Turn identities already used by ordinary launch.

After writing the plan, the coordinator does not reread the mutable target Agent or parent
capability snapshot. It uses the existing `PrepareJob -> EnsureInitialLaunch -> SubmitJob` fences,
extended into one launch pipeline with reservation, final check, and abort:

```text diagram
persist request fence with caller intent + target + exact prompt + resolved child execution fingerprint
  -> pre-plan validation
  -> keep validation-pending with no child artifacts, terminally preplan-reject with no child artifacts,
       or persist launch plan and reserve EdgeId
       at SessionTreeMutationFence
  -> prepare child AgentJob with pinned RunnerId and child workDir
  -> create child AgentSession(workDir immutable) + initial SessionInput + initial AgentTurn
  -> final check reservation, parent workDir, binding, stop admission
  -> finalize the child-owned SessionParentLink through the fence mutation protocol
  -> submit the same prepared AgentJob to its pinned Runner
```

If pre-plan validation observes temporary unavailability, `SpawnRequestFence` remains
`validation-pending`. It persists no launch plan, reservation, or child identity and creates no Job,
Session, Input, Turn, or link. Same-key retry revalidates these facts until the request advances to
an admitted plan or the terminal `preplan-rejected` described above. The latter likewise creates no
child artifact, but same-key retry replays only that fixed result; only a new key expresses a new
delegation.

Once persisted, a plan contains immutable child identities, expected workDir/binding, and
`LinkReservation`. Same-key replay can recover only its original result and cannot choose a new
parent, target, workDir, or Runner. Every later command has a stable command identity; repeated
execution must return an already-applied acknowledgement. The coordinator reminder recovers from
that durable fence.

A final-check or abort rejection after planning is a durable terminal outcome. Even if the parent
binding, workDir, or cascade-stop admission later recovers, same-key replay converges only on the
plan's original result. A new key is required for later delegation.

The reservation claims EdgeId but is neither a tree edge nor external child visibility. Before the
final check, child Job, Session, Input, and Turn are provisional artifacts. `mo session tree`, the
normal spawn success response, and ordinary Session commands cannot discover or operate them. The
initial Turn may be queued and Session activity may therefore be active, but that means only that
the coordinator is recovering this plan; it never means work was submitted to the Runner.

Finalizing attachment must compare the parent's expected workDir, binding, and stop admission again
inside `SessionTreeMutationFence`. The child writes attached `SessionParentLink`/index at the
assigned revision and returns the exact receipt. Only after the fence validates acknowledgement and
`Commit`s does the child become visible to tree reads and the success response.
`SubmitPreparedLaunch` must also carry the plan's attached reservation, and every submit/recovery
path validates it. Without an attached reservation, or after the plan enters a rejection fence,
the result is always `must-not-submit` even if the Job is still pending.

If the final check finds parent reset, changed workDir/binding, reservation rejection by a stop
snapshot with reason `parent_tree_stop_in_progress`, or another unrecoverable conflict, it first
persists the plan as `rejected`, then converges provisional artifacts through a stable abort
command. The reservation becomes `rejected`; the prepared Job becomes terminal `cancelled` with
reason `parent_link_rejected`; the initial Turn becomes terminal `cancelled`; and Session activity
returns to `idle` while AgentSession remains nonterminal. A written initial Input remains only for
audit of the rejected plan and is invisible to ordinary Session input, tree reads, and launch
success. There is no `SessionParentLink`, so this cancellation is not a terminal report for an
accepted delegation and never appends an Input to the parent.

Abort itself may be interrupted after any participant call. Replaying the same abort command must
complete remaining participants and preserve the same cancelled/rejected result. If rejection
occurs before a provisional artifact is created, that artifact remains absent while all created
artifacts converge as above. Replay returns the same durable rejected outcome and reason without
exposing ordinary successful Job/Session references for an unaccepted child. Replaying a plan whose
attachment finalized successfully does not change the child because the target was renamed or
archived or the parent configuration was edited.

The child Job dispatch envelope carries `PinnedRunnerId`. AgentJob admission may claim only that
Runner. If it is unavailable, the Job retains ordinary pending/retry state and cannot migrate
through Project-wide eligible-Runner selection. The Runner still receives the resolved prompt,
workDir, runtime, and binding constraint and does not choose the parent, resolve capability, or
materialize an arbitrary path.

## Startup-known context

For every Agent launch, the Server creates an immutable `AgentSessionStartup`, persists it with the
execution definition, and supplies it to the Agent as Runtime-supported startup/system context
before the first dispatch. It differs from user-provided read-only `AgentStartupContext` and cannot
borrow the latter's external-discussion semantics.

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

This Server-originated context must be visible before the task prompt. It states explicitly that
the Agent decides target selection, brief generation, unique-key generation, waiting, retries, and
acceptance. The child also receives ParentSessionId and can request help through ordinary
`mo session followup <parent-session-id>`.

Do not set `MOHIST_SESSION_ID` or any per-session process environment variable. If a Runtime has no
system-context channel, the Runner can receive only a startup block explicitly marked by the
Server. It still cannot infer identity by reading the environment, workDir, or session files.
Session settings from the initial launch are the sole snapshot source for restart and follow-up.

## Messages and child terminal report

### Ordinary cross-session input

Parent-to-child steering and child-to-parent requests for help both use existing
`mo session followup` and the canonical `SessionInput` / `AgentTurn` path. The tree relationship
provides only discoverable Session IDs and startup context; it invents no message format, separate
queue, or special Runtime protocol in either direction.

### Authoritative terminal trigger

AgentSession never enters a terminal state. A child delegation becomes terminal when its spawned
`ChildLaunchJobId` enters `completed`, `failed`, or `cancelled`. `unknown` is not terminal, and a
terminal initial or later `AgentTurn` is not a report trigger.

In the same state transition, AgentJob persists a terminal event with SpawnOrigin. The event
carries child Job/Session ID, status, initial Turn ID, result-observation reference, and EdgeId. It
does not write the parent or copy complete transcript, output, or natural-language result into the
parent. The event handler then claims the report on the child-owned link, so `TerminalReport` in
that link is the sole state of the delivery obligation.

### Durable idempotent delivery

The Server at-least-once event handler processes a terminal report in this order:

```text diagram
AgentJob terminal event
  -> child Session atomically claims report on its attached link
  -> append a normal parent SessionInput
  -> parent Session accepts/reuses its AgentTurn according to normal rules
  -> child link records delivered parent InputId
```

Claim and `attached -> detached` compete in one child Session transaction:

- if detach commits first, claim becomes `suppressed` and no report is generated;
- if claim commits first, report becomes `pending` and later detach does not revoke the report for
  a delegation that already happened;
- detach never deletes a delivered parent Input.

The parent Input uses deterministic idempotency key
`subagent-terminal:{edgeId}:{childLaunchJobId}`, source `subagent-terminal`, and structured
provenance containing child session/job/turn/result references. Its visible body states only the
child, terminal state, and where to query the result. Duplicate events, activation loss, and handler
retry therefore create at most one logical SessionInput; same-key replay returns the original Input
and Turn.

When the parent is idle, this Input creates a new Turn as an ordinary follow-up. When the parent is
active, it enters the current or later Turn in normal input order. If parent input capacity,
unknown activity, or temporary unreachability blocks delivery, the link remains `pending`. The
Server retries with the same key when parent capacity/activity changes and during dispatcher
recovery. It never silently discards the report or rolls the terminal child AgentJob back to
incomplete. Report delivery and the child Job result are separate durable facts.

## Cascade stop and detach

### Cascade stop

`mo session stop <session-id> --idempotency-key <key>` creates a
`SessionTreeStopOperation`; it does not mark any AgentSession as stopped. The public stop command
accepts only Project, root Session, and idempotency key. The Server derives the operation ID and
request fingerprint; the caller cannot supply graph revision, membership, or targets. It creates a
readable operation resource keyed by root Session and idempotency key. Same-key retry recovers the
same operation; only a new key represents a new stop request.

The Server selects revision `R` through a `SessionTreeMutationFence` command ordered after all
earlier fence mutations, persists the materializing operation identity, and invokes a stateless,
revision-pinned internal snapshot source. The source reads edges from the child-owned
`SessionParentLink` projection where `AttachedRevision <= R` and `DetachedRevision` is null or
greater than `R`. It generates deterministic breadth-first membership and reads each member's
durable current turn/binding and stable child stop-operation ID. It stores no topology and accepts
no client- or Runner-submitted member, Turn, or binding, so it is not a second topology authority.
Only after the fence persists root, membership, `R`, and targets returned by the source does it
publish the stop snapshot.

Same-command replay during materialization recovers the same operation and `R` and drives the
unfinished source read again. Facts not yet persisted are not an accepted snapshot. After snapshot
publish, replay returns only persisted membership and targets without traversing the tree or
selecting bindings again. A started attachment/detach mutation must recover through publish before
materialization begins. The same fence also handles reservation, final attachment, and detach, with
this ordering:

| Operation linearized first | Consequence |
|---|---|
| Detach before stop snapshot | The subtree is outside the snapshot; the later stop does not affect it. |
| Finalized attachment before stop snapshot | The child is in the snapshot; ordinary target rules handle its current work. |
| Stop snapshot before detach | IDs of the then-attached subtree are frozen in the snapshot; later detach does not remove them and the snapshot still stops them. |
| Stop snapshot encounters an earlier unfinalized reservation whose parent is in membership | Mark the reservation rejected; exclude the child from the snapshot; the coordinator may only abort and never submit. |
| New spawn, reservation, or final attachment inside membership while a published stop operation is nonterminal | A request fence without a plan returns `parent_tree_stop_in_progress` and remains `validation-pending`; same-key retry revalidates after the operation becomes terminal. An existing plan/reservation may only abort and never submit. |

Concurrent operations therefore have no intermediate state that "reads a changing tree." Stop
retry recovers from persisted target IDs, expected turn/binding, and child stop-operation IDs. It
does not traverse the tree again or reconsider whether detach or attachment belongs to this stop.

If the snapshot includes an attached child that has not yet been Submitted, its target
sub-operation cancels that queued work by initial Job/Turn identity and writes cancellation into
the same plan's submission gate. The coordinator and reminder can never submit afterward. The link
has already been accepted, so the Job's terminal `cancelled` result still follows normal terminal-
report rules. This differs from abort after reservation rejection, which has no callback.

Each target executes only existing Server turn-control semantics:

| Target state at snapshot | Operation result |
|---|---|
| No nonterminal Turn | `already-idle`; Session continues to exist. |
| Queued Turn | `cancelled`; do not contact the Runner. |
| Executing Turn | Server requests stop from the target's expected-binding Runner and records `stop-requested`. |
| Runner did not receive the request | `pending`; safely retry the same sub-operation. |
| Runner may have acted but the result is unconfirmed | `unknown`; never fabricate idle or cancellation. |
| Target replaced binding/Turn | `rejected`; do not stop work that appeared later. |

Operation summary derives only from per-target facts: all determinate targets produce
`completed`; a mix of determinate success and `rejected` produces `partial`; any unconfirmed
result produces `unknown`; remaining safe delivery produces `running`. Only `completed` and
`partial` are terminal outcomes. Both `running` and `unknown` retain the stop-admission fence for
snapshot membership. Unknown is not safe completion and must retry/reconcile the original
operation to a determinate result. During that time, new spawns in membership remain rejected with
`parent_tree_stop_in_progress`. Retry only rechecks or resends the same persistent sub-operation ID
for the original target. It does not traverse the tree, create a child, replay input, or stop a
later Turn outside the operation snapshot.

Session therefore has no terminal lifecycle. Cascade stop ends current work in the snapshot; it
does not close a session. After completion, parent or child can continue through another explicit
follow-up. That creates a new Turn that the old stop operation does not pursue.

### Detach

`mo session detach <child-session-id>` requires a currently attached child. It changes the child
Session link to `detached` as a fence mutation. The fence first persists the complete detach tuple
and assigned revision. The child then idempotently transitions and writes the detach revision/index
receipt with the same command, edge, parent/child identity, child launch Job, expected attached
revision, and assigned revision. The fence accepts only this exact receipt and finally publishes
with the same command, edge, and revision. It returns the removed parent and edge identity without
cancelling the child, moving workDir, changing child Source, or converting the child into a new
Agent resource. The detached child is a new tree root whose attached descendants retain their
structure.

Detach is an idempotent target state. Retrying an already detached child returns the historical
link. It cannot reparent to another Session. A wrong revision or mismatched receipt cannot rewrite
the historical link or advance the graph; it enters `ReconciliationRequired` instead of retrying
with another command or revision. [Durable idempotent delivery](#durable-idempotent-delivery) is the
sole authority for the race between terminal report and detach.

`PendingDetach` is the durable production-recovery work record; there is no separate detach-
operation grain. Every `SessionTreeMutationFence` has one fixed periodic recovery reminder as an
at-least-once wake-up. `BeginDetach` can be accepted and its tuple/assigned revision written only
after that reminder is registered or updated successfully. The reminder is not cancelled when
there is no pending work or after one commit; it no-ops with no pending work. Every fence activation
also ensures the reminder is registered, so activation loss cannot leave a detach without a
wake-up. The reminder drives child `ApplyDetach(exact tuple)`, persists the exact acknowledgement,
then `Commit`s with the same command/edge/revision. Temporary unreachability retains pending state
and retries the same tuple. A mismatched receipt enters reconciliation and cannot try a new
revision.

The four recovery windows behave as follows: call the child when Begin is written but the child was
not called; call the child again for an already-applied receipt when the child wrote but the fence
did not acknowledge; only commit when acknowledgement is written but `Commit` is not; and return
the historical result when commit is written but the caller did not receive it. A reminder tick
after commit sees no pending work and no-ops. Replay or an extra reminder cannot call the child
again or advance the graph.

## Scheduled input interaction

Scheduled input is owned by the target AgentSession and is authoritative in
[`scheduled-input.md`](scheduled-input.md). A parent can create a schedule for a child through the
ordinary Session API, but the tree link neither owns the schedule nor changes its delivery identity.

- **Cascade stop:** a schedule is not part of the frozen stop snapshot and is never deleted or
  cancelled by it. Delivery waits while the stop operation is nonterminal, then may create a later
  Turn outside that snapshot.
- **Detach:** changing `SessionParentLink` does not change a schedule's target Session. A detached
  Session still receives its scheduled Input when due.
- **Spawn:** a schedule addresses an existing Session. It never launches a child at due time.

## Non-goals

- Do not introduce an Agent resource hierarchy, temporary identity-free Agents, or cross-Project
  spawn.
- Do not treat Session as completed, failed, stopped, or closed.
- Do not create fork-join, automatic retry, automatic summarization, Agent recommendation, task
  planning, acceptance, or parent-agent patrol mechanisms.
- Do not copy transcript, output, tool calls, or Runtime context into the parent. A terminal report
  carries references only.
- Do not accept `--work-dir`, `workspacePath`, a named Workspace override, or any filesystem path
  input. The child directory comes only from the inherited parent Workspace
  ([`workspace.md`](workspace.md)).
- Do not let the Runner infer parent/child identity by scanning working directory, environment
  variables, or Runtime Session, or choose a Runner for the child that differs from the parent
  binding.
- Do not claim that the tree relationship replaces Issue, Workflow, or Project Space ownership and
  isolation models.

## Verification

Server specs must cover at least these behaviors with a fake Runner, fake clock, and in-memory
stores:

- capability snapshot outcomes for rename, archive, self-spawn, cross-Project, and same-key
  conflict;
- SpawnOrigin parent identity; `validation-pending` observation for missing/unknown/stale binding;
  same-key revalidation; terminal pre-plan rejection for missing authoritative workDir; workDir
  inheritance and exact Runner pin; and proof that Job admission cannot choose another eligible
  Runner;
- a controlled reset-versus-acquire race for parent `BindingEpoch`/`BindingUseReceipt`: when reset
  linearizes first, reject/abort the plan; when acquire linearizes first, reset cannot replace the
  binding. Attach receipt must match the complete tuple field by field before publish or release;
- terminal pre-plan workDir/authorization/archive/NeedsSetup/Unknown-catalog rejection
  (`agent_needs_setup` or `execution_catalog_unavailable`) persists only the request fence,
  supports stable same-key replay and mismatched-payload conflict, while actual temporary parent
  binding observations revalidate under the same key without a child artifact. Post-plan
  reservation/final-check rejection produces cancelled Job,
  cancelled initial Turn, idle Session, no visible link/input/callback, and replay must-not-submit;
- activation loss/retry at every durable coordinator fence produces exactly one Job, Session,
  Input, Turn, edge, and dispatch, or the same durable rejection;
- one parent, no reparent/cycle, indexed read cost for subtree query after detach, batching,
  revision-pinned stable BFS page/continuation, and concurrent attach/detach visible only to a new
  query;
- every otherwise eligible parent source is accepted when it has authoritative workDir and a
  current Runner binding, while Workflow inline and any parent missing required facts are rejected.
  When target Agent `MaxConcurrentRuns` is full, the child queues under ordinary Job semantics
  instead of being rejected;
- AgentJob terminal, not Session/Turn terminal, triggers one parent SessionInput, including handler
  replay, parent busy/capacity, unknown, and detach races;
- the minimal deterministic tree lifecycle matrix: sequential coexistence of multiple `Reserved`
  values without graph changes, strictly increasing finalize revisions, and atomic snapshot
  rejection of unfinalized reservations in membership. Controlled barriers and `Task.WhenAll`
  prove that while attach/detach awaits acknowledgement, a second finalize or snapshot cannot pass
  the pending mutation, while a new invisible reservation assigns no revision;
- stop snapshot source generates root, deterministic BFS membership, durable turn/binding, and
  targets only from the revision-pinned child-owned projection. Public commands can neither submit
  nor omit them. The first published fence command decides snapshot/detach concurrency, and later
  detach does not change frozen targets;
- any mismatch in command, edge, parent/child identity, child launch Job, or assigned revision on an
  attach/detach participant receipt prevents publish and graph advancement and enters
  reconciliation. The four recovery windows replay only the same tuple after activation loss/replay
  and publish exactly once;
- detach does not depend on CLI retry. Begin is accepted only after fence recovery-reminder
  registration is ensured. The reminder independently recovers or returns the historical result
  after activation loss following Begin, child apply, acknowledgement, or commit. Activation
  reregisters it, and a tick with no pending work changes no state;
- revision-pinned tree/stop source queries each frontier raw-candidate-first. A reachable malformed
  child, duplicate, or cycle returns `session_tree_projection_inconsistent`, no partial tree, and no
  persisted stop snapshot/targets;
- cascade target queued/executing/idle/unknown, pre-submit child cancellation, partial outcome,
  same-operation retry, unknown retaining the membership stop-admission fence, and a later Turn
  not stopped by the old operation;
- startup context contains own Session ID, parent ID when present, snapshot, and canonical CLI
  command before the first turn, and dispatch does not depend on a per-session environment variable;
