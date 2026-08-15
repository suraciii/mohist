## Context

The first slice of this change is on master and inert: `WorkflowAgentHandoffGrain`
persists one rendered handoff command per work attempt keyed by
`(commandId, projectId, workflowRunId, taskRunId)`, fingerprints the rendered input,
freezes the resolved `AgentExecutionDefinition` and minted
Job/Session/Input/Turn identifiers on first preflight, persists definitive
rejections (`agent_not_found`, `agent_runtime_unavailable`, invalid input), and
stores an idempotent acceptance receipt. `Prepared` and `Accepted` create no
AgentJob, AgentSession, SessionInput, AgentTurn, or Runner work. Nothing calls
it yet.

The live `mohist/agent` path is still inline: `WorkflowItemTranslator`
(`ResolveAgentTaskAsync`) resolves the Agent at dispatch time, rewrites the task
to `mohist/opencode` / `mohist/pi` with `{prompt, session?, timeout?}`, and
attaches the definition snapshot to a Workflow-owned `WorkDispatch`. The Runner
executes it as a TaskRun action, evaluates task-level `expect` against the
workspace, applies `setVars` via the run-variables patch, uploads artifacts,
and reports a `TaskReport` through `/report` → `WorkflowReportService` →
`WorkflowGrain.ReceiveTaskReportAsync`. The TaskRun is the work owner; no
AgentJob exists, so Workflow and Session surfaces cannot find each other's
records.

Direct Agent launches already have the target ownership model:

| Fact | Owner | Mechanism |
| --- | --- | --- |
| Agent admission, Runner claim, execution, terminal | `AgentJobGrain` | ledger row + `AgentJobState`, poll-time `ClaimNextAsync`, `EnterTerminalStateAsync` with durable pending obligations and a recovery reminder |
| Conversation facts (Inputs, Turns, transcript, binding) | `AgentSessionGrain` | `EnsureInitialLaunchAsync` materializes session + first Input + first Turn idempotently under caller-minted ids |
| Launch orchestration | `AgentLaunchCoordinatorGrain` | durable `Pending` step machine + recovery reminder + participant probe; adopts pre-minted Session/Input/Turn ids via `PrepareManualLaunchAsync` |
| Cross-participant typed delivery | event bus | durable CloudEvents (`com.mohist.agent.job.*`) with stable ids, dead-letter redelivery, idempotent handlers (e.g. `AgentJobSubagentTerminalHandler`) |
| Workflow task completion effects | `WorkflowGrain` + domain | `ApplyTaskReportAsync` (artifacts → settlement → advancement), `FailTask` recovery, `BindTaskReportArtifactsAsync` |

Constraints: no second queue, scheduler, or direct Runner-process control for
handoffs; the Agent execution must not use the Workflow task-report endpoint as
a transport; sibling paths (direct launches, inline `mohist/opencode` /
`mohist/pi`, runtime adapters, Slack Bot / external Agent API) stay unchanged;
no new external dependencies; injectable time; explicit state machines instead
of polling.

## Goals / Non-Goals

**Goals:**

- Complete the path from the delivered fence: an accepted receipt materializes
  the reserved AgentJob, AgentSession, first SessionInput, and first AgentTurn
  idempotently, using only minted identifiers and the frozen definition.
- Execute workflow-originated AgentJobs through the existing AgentJob admission
  and scheduling boundary (shared readiness, workspace resolution, per-Agent
  concurrency permits, Runner claim through the AgentJob ledger).
- Deliver the AgentJob terminal to a Workflow-owned finalizer through typed,
  replayable transport; the finalizer applies `expect`, `artifacts`, `setVars`,
  and recovery effects exactly once with completion-effect receipts.
- Cut new `mohist/agent` dispatches over to the handoff path with the task
  input contract (`name`, `prompt`, `session`, `timeout`) unchanged.
- Expose one stable invocation status (`queued`, `executing`, `completed`,
  `failed`, `cancelled`, `recovering`) plus the Job/Session/Input/Turn
  identifiers and final result on both Workflow and Agent/Session read
  surfaces, linked without parsing transcripts.

**Non-Goals:**

- No changes to direct Agent launches (Web UI, CLI, event routing, mentions,
  connections), inline `mohist/opencode` / `mohist/pi` tasks, runtime adapters,
  or the Slack Bot / external Agent API surfaces.
- No new queue, scheduler, runner process control, or dispatch transport for
  handoffs; the AgentJob claim path is reused as-is.
- No Workflow task-report endpoint changes and no Agent facts encoded as task
  report payloads.
- No Agent definition mutability work: post-acceptance edits affect only new
  invocations, by design.

## Decisions

### D1. The handoff grain becomes the activation process manager

`WorkflowAgentHandoffGrain` gains an `ActivateAsync` command and a durable
activation step machine, mirroring `AgentLaunchCoordinatorGrain`: a persisted
`Activation` cursor (next step + command id), a recovery reminder that resumes
the cursor on activation or crash, and an `IWorkflowAgentHandoffParticipantProbe`
seam (no-op in production) so specs can simulate acknowledgement loss after a
durable participant write.

Steps, in the launch-coordinator order (job input before session
materialization, submit only after both):

1. `PrepareJob` — `AgentJobGrain.PrepareManualLaunchAsync` under the minted
   `JobKey`, adopting the minted Session/Input/Turn ids. Durable input, no
   Runner work.
2. `EnsureInitialLaunch` — `AgentSessionGrain.EnsureInitialLaunchAsync` under
   the minted `SessionId` with the frozen `AgentExecutionDefinition`, the
   prompt, and workflow lineage labels; creates the AgentSession, first
   SessionInput, and first AgentTurn idempotently.
3. `SubmitJob` — `AgentJobGrain.SubmitPreparedLaunchAsync` → shared admission.

On completion the disposition advances to a terminal `Activated`. Replayed
activation of an `Activated` plan is a no-op that returns the invocation.
`Rejected` and `Prepared` plans never activate; a `Rejected` plan replays its
frozen rejection.

Alternatives: a separate activation grain (splits one durable plan across two
storages and needs cross-grain fencing for no benefit); reusing
`AgentLaunchCoordinatorGrain` (keyed by `(projectId, idempotencyKey)` with
manual-launch concerns — parent links, spawn fences, connection origins — that
a workflow handoff does not have); direct activation calls from the translator
without a durable cursor (a crash between participants strands reserved ids
with no resumable owner).

### D2. The plan freezes agent identity and the attempt execution contract at first preflight

The frozen `AgentExecutionDefinition` is insufficient for activation:
`AgentJobInput.AgentId` is required (concurrency gate is keyed
`GrainKey.Agent(projectId, agentId)`, dispatch lineage carries `agentid`), and
the AgentSession needs `AgentName` labels. The handoff preflight therefore also
freezes the resolved `AgentId` and `AgentName` next to the definition, and the
command/plan gains the attempt-scoped execution facts the boundary needs:

- `Expect` — the persisted task-level completion contract for this attempt
  (immutable on the task definition; freezing it keeps one canonical snapshot
  per invocation).
- `TimeoutMilliseconds` — already in the command; it becomes the per-invocation
  execution deadline (D4).
- `SessionName` — the logical session name (`session`, defaulting to the work
  id), stamped as a session label so named-session lookup keeps working.
- Workspace identity — `issue-{issueNumber}` when the run is issue-linked,
  else the run's workspace identity; resolved once from the run snapshot at
  prepare, never re-read.

The canonical fingerprint extends to cover the added rendered fields, so a
conflicting re-render of any frozen fact is a conflict, not a mutation.
Nothing calls the fence yet, so extending the delivered record shape is a
compatible edit, not a migration.

Alternative: re-resolve identity/workspace/expect at activation — rejected;
it re-reads mutable state (Agent renames, task edits on retry drafts) and
breaks the "replay never re-reads mutable configuration" contract the fence
already guarantees for the definition.

### D3. Materialization reuses the manual-launch AgentJob entry points with pre-minted ids

`PrepareManualLaunchAsync` already does what activation needs: it requires
caller-minted `SessionId`/`InputId`/`TurnId`, persists `ManualPlan` + input
idempotently (`PlansEquivalent` replays return the stored input; conflicting
plans throw), keeps the job `Visible` by default (no provisional-launch step —
a workflow handoff has no parent link or approval gate), and correlates the
initial Input/Turn ids so the runner and `MarkInitialTurnTerminalAsync` use
them. `SubmitPreparedLaunchAsync` then flows into `TryAdmitAsync`.

A positive workflow discriminator is added to `AgentJobInput`
(`WorkflowInvocation`: invocation id, task run id, work id) so the grain can
distinguish handoff jobs from direct launches — `WorkflowRunId` alone is not
sufficient because routed launch plans also carry it. The discriminator drives
only D5 transport staging and lineage; it introduces no branch in admission or
execution.

Alternatives: a new `SubmitWorkflowInvocationAsync` on `AgentJobGrain`
(duplicates the manual-launch machinery with a different label); the routed
plan path (`EnsurePreparedAsync`/`AdvancePreparedLaunchAsync` — event-router
shaped, no minted Input/Turn ids, re-reads session open semantics).

### D4. Shared admission, unchanged; per-invocation deadline feeds the job timeout

Activation submits through the existing boundary: `IAgentConcurrencyGrain`
permits (shared with direct launches — a workflow job waits under the same
per-Agent gate), workspace home-runner affinity, runner slot election, ledger
admission, and poll-time `ClaimNextAsync` through
`RunnerGrain.TryClaimAgentJobAsync` and `DispatchService`'s agent-job ledger
poll. No scheduler, queue, or runner notification is added for handoffs.

The frozen `TimeoutMilliseconds` (defaulting to the runtime action default,
3600000 ms, matching inline `mohist/opencode`/`mohist/pi` semantics) becomes a
per-invocation deadline on `AgentJobInput`; `ArmJobTimeout` uses it when
present so a long agent turn is not prematurely failed by the global
`AgentJobOptions.JobTimeout` backstop (10 min), while an explicit short
`timeout` still bounds the execution exactly as the task declared.

### D5. Typed terminal transport is a durable CloudEvent obligation, mirroring the subagent-terminal pattern

`AgentJobState` gains a `PendingWorkflowTerminalDelivery` obligation, staged in
`EnterTerminalStateAsync` only when the input carries the `WorkflowInvocation`
discriminator. It is emitted as a new catalog type
`com.mohist.agent.job.workflow-terminal` with a stable event id
(`workflow-terminal:{jobKey}`) and a typed payload:

- invocation identity (invocation id, project, workflow run, task run, work id,
  job/session/input/turn ids),
- terminal facts (status, output, failure reason/category, exit code, artifact
  upload ids),
- the completion evaluation computed at the boundary (D6),
- recorded timestamp.

Delivery follows the established pattern: the AgentJob recovery reminder
retries until the event store append succeeds, then clears the obligation; the
event bus provides at-least-once delivery with dead-letter redelivery; the
consuming handler is idempotent against the finalizer receipts. Duplicate or
redelivered terminals resolve against the same invocation identity without a
second outcome.

Alternatives: a synchronous `AgentJobGrain → WorkflowGrain` call inside the
terminal transition (couples the Agent grain to Workflow, no replay semantics
without inventing a second delivery protocol); the Runner reporting the Agent
result to the Workflow task-report endpoint (explicitly forbidden — it makes
the Workflow endpoint an Agent transport channel); `WorkflowGrain` awaiting
`WaitForTerminalAsync` (polling/waiting across owners instead of a durable
signal, and it leaves no replayable delivery record).

### D6. Completion evaluation is computed at the execution boundary, applied by the finalizer

Task-level `expect` checks workspace files and markers — a filesystem fact only
the Runner's executor has. The runner's agent-job executor therefore evaluates
the frozen `expect` (carried on the existing `WorkDispatch.Expect` field, which
already documents "the Workflow task executor reads and evaluates this after
the Action returns") after the agent turn settles, reusing `evaluateCompletion`,
and reports the typed evaluation (satisfied, matched promise, missing
files/markers, `failIf` matches, message) alongside the agent result. The
evaluation rides the terminal facts into the D5 transport; the finalizer owns
the *decision*: a completed AgentJob with an unsatisfied evaluation fails the
task with the same `expectation-failed` code and message the inline path
produces, and the matched promise projects into the task output
(`output.promise`) exactly as the inline executor does.

The AgentJob terminal itself stays the Agent execution verdict — an agent that
completed stays `completed` even when the Workflow expectation fails. This
keeps "the AgentJob owns the Agent execution lifecycle and its result" honest
on both surfaces.

Alternatives: server-side evaluation (impossible — the server has no workspace
filesystem access, by architecture constraint); failing the AgentJob terminal
on expectation miss (conflates Agent execution ownership with Workflow
completion policy and misreports the Agent surface); a second
Workflow-owned verification dispatch per task (extra runner work and a second
claim cycle; rejected as YAGNI).

### D7. The finalizer is Workflow-owned, on `WorkflowGrain`, with per-effect receipts on the TaskRun

A `WorkflowGrain.SettleAgentInvocationAsync` command (invoked by the D5
subscription handler, and by run reconciliation) validates the attempt via
`FindReportableWork`, then applies effects in the same order the inline
executor uses, guarded by a durable `AgentInvocationSettlement` receipt record
on the TaskRun (terminal delivery id, per-effect applied flags, frozen terminal
snapshot):

1. artifact upload binding (`BindTaskReportArtifactsAsync` — same service and
   pending-upload repository as inline reports),
2. settlement — success: `ApplyTaskReportAsync` (complete task, resolve
   feedback, advancement); failure or unsatisfied expectation: the domain
   `FailTask` path so recovery `when` matching and the remaining recovery
   budget keep their existing semantics,
3. `setVars` extraction from the settled output and application to run
   variables through the same store the runner's patch route uses (extraction
   or patch failure fails the task with the existing `setVars:` message),
4. advancement — already part of the domain settlement calls; the receipt
   marks the invocation settled.

Replay of the same terminal delivery is acknowledged as already-applied from
the receipt without reapplying any effect; a stale terminal for an attempt that
is no longer reportable (task already terminal, run stopped/deleted) is
acknowledged, not applied. An interruption between receipt persistence and the
last effect resumes from the recorded flags — driven by the existing
activation-reconcile + reminder pattern (`agent-result-settlement` shows the
way; the finalizer gets its own reminder for unsettled invocations).

Workflow control joins the same linkage: `StopAsync` cancels a running handoff
invocation's AgentJob (`CancelAsync`) alongside the existing unresolved-agent
handling, and the cancelled terminal settles the task under the existing stop
semantics. Workflow advancement never leaves the Workflow grain; the AgentJob
never applies a task effect.

Alternatives: a standalone finalizer grain (a second owner of task effects that
must then call back into `WorkflowGrain` for every effect — more indirection,
same single-writer requirement); reusing `ReceiveTaskReportAsync` directly with
a synthetic `TaskReport` (loses per-effect receipts and the
expectation/setVars ordering the spec demands).

### D8. Dispatch cutover lives in `WorkflowItemTranslator`, gated on the delivered order

Where `ResolveAgentTaskAsync` currently rewrites `mohist/agent` to an inline
runtime action, the translator instead renders the handoff command (identity
from the work id/run/task attempt, input from the persisted `with`), drives
`Prepare → Accept → Activate` on the handoff grain, and returns a *delegated*
outcome instead of a `WorkDispatch`:

- A durable rejection surfaces as the same `WorkflowDispatchRejectedException`
  with the same codes (`agent_not_found`, `invalid_agent_input`) the inline
  path throws, so dispatch rejection and task failure behavior are unchanged.
- No Workflow dispatch snapshot is stored for the task; the runner's existing
  agent-job ledger poll claims the job after admission.
- The translator records the invocation linkage on the TaskRun (D9) in the
  same claim-time write.

Input validation (non-empty `name`/`prompt`, positive `timeout`, task-only
usage) keeps its existing static checks in the manifest/profile layer and is
re-validated by the durable preflight. The switch is made only in the final
slice — after participants, transport, and finalizer exist — so no handoff
work is ever dispatched without an owner for its completion effects.

Alternatives: cutover inside `WorkflowGrain.ClaimNextAsync` (moves Agent
resolution into the control-plane grain the translator was extracted from);
an availability flag flipping the path at runtime (two live semantics behind
a switch, against the no-compatibility-layers constraint).

### D9. Linkage is persisted once at dispatch; status is derived, never mirrored

The TaskRun persists the immutable invocation linkage (invocation id +
minted Job/Session/Input/Turn ids) at handoff time, making the Workflow run
the queryable source for the Workflow surface. The AgentSession carries the
reciprocal lineage as metadata labels at materialization (project, workflow
run, task run, work id, invocation id, logical session name) — the same label
infrastructure the inline path uses — so Agent/Session surfaces resolve the
owning WorkflowRun/TaskRun by label without a grain read. Both surfaces
resolve to one execution identity.

Invocation status is a derivation over authoritative records, not a stored
mirror:

| Condition (derived from AgentJob ledger + receipt + task state) | Status |
| --- | --- |
| activated, admitted but not claimed (including permit/runner waiting reasons) | `queued` |
| claimed and running; AgentJob `Unknown` (outcome unconfirmed, not terminal) | `executing` |
| AgentJob terminal `Completed` / `Failed` / `Cancelled` | `completed` / `failed` / `cancelled` |
| failed terminal consumed, recovery decision pending or applying (matched handler, budget remaining) | `recovering` |

The final result (output, failure reason) is read from the AgentJob terminal
facts once terminal — no transcript parsing.

Alternatives: storing a status mirror on the handoff plan (dual-write staleness
across three owners; the plan is immutable linkage by design); deriving status
by reading the handoff grain from read routes (grain reads on every status
query; the run already owns the linkage after D9).

## Risks / Trade-offs

- [Runner runtime view double-counts a delegated task] A running handoff task
  projects as an active Workflow work item *and* its AgentJob projects as an
  agent-job work item in `BuildRuntimeStateAsync`, inflating slot counts and
  the active-work view -> the delegated running task is marked agent-owned on
  the run so the runtime view suppresses its workflow projection and the
  AgentJob remains the single active-work entry.
- [Frozen snapshot diverges from live Agent edits after acceptance] An Agent
  edited between acceptance and execution still runs the old definition ->
  accepted: this is the specified freeze semantics; only newly dispatched
  invocations resolve edits, and the docs state it.
- [Two settlement code paths drift (inline for `mohist/opencode`/`mohist/pi`,
  finalizer for `mohist/agent`)] Effects could diverge over time -> both paths
  converge on the same domain calls (`ApplyTaskReportAsync`, `FailTask`,
  artifact bind service, variable store); the finalizer adds only receipts and
  ordering, not a second effect implementation. Specs assert the same outcomes
  for an inline task and a handoff task given the same terminal facts.
- [Transport delivery stalls in dead-letter] A terminal event that repeatedly
  fails handling delays task settlement -> handler is idempotent and cheap;
  the finalizer reconcile reminder independently resumes unsettled
  invocations from receipts, so settlement does not depend solely on event
  delivery; dead-letter surfacing makes the stall visible.
- [Global `JobTimeout` backstop (10 min) kills long agent turns] A workflow
  agent execution legitimately exceeding the backstop enters `Unknown` ->
  the per-invocation deadline from the frozen `timeout` (default 60 min,
  matching inline) overrides the backstop in `ArmJobTimeout` (D4).
- [Stop/cancel window] A run stopped between acceptance and job claim, or a
  job cancelled while the finalizer is mid-settlement -> stop cascades through
  the linkage to `AgentJob.CancelAsync`; the cancelled terminal settles under
  existing stop semantics; receipts make the mid-settlement case resumable
  exactly once.
- [Session continuation semantics change for named `session` inputs] Inline
  tasks sharing a logical session name continue one AgentSession; per-attempt
  minted sessions may not -> open question O1; whichever answer ships is
  documented as part of the BREAKING cutover.

## Migration Plan

Delivery follows the tasks.md order; each slice is independently deployable
and leaves the previous behavior live until the final switch:

1. **Fence (shipped).** Command, invocation, preflight, receipt. No callers.
2. **Participants.** Freeze extension (D2), activation step machine (D1),
   manual-launch reuse with pre-minted ids and the workflow discriminator
   (D3), per-invocation deadline (D4). Inert until called; grain-spec coverage
   for idempotent activation, replay without duplication, unaccepted plans
   never materializing.
3. **Transport.** `PendingWorkflowTerminalDelivery` + event type + handler
   (D5), boundary completion evaluation (D6). Staged only for jobs carrying
   the discriminator — none exist yet, so the path is dark until slice 4.
4. **Finalizer.** `SettleAgentInvocationAsync`, receipts, reconcile reminder,
   stop cascade (D7). Consumes only transport events — still dark.
5. **Cutover.** Translator switch + TaskRun linkage (D8/D9), read-surface
   projections, docs updates (`docs/actions/agent.md`,
   `docs/agent-sessions.md` "Two Invocation Paths",
   `design/agent-execution.md` ownership paths). New dispatches use the
   handoff; already-dispatched inline attempts finish on the inline path —
   no in-flight migration.

Rollback: revert slice 5 (the switch). Accepted-but-unactivated receipts and
their reserved identifiers remain inert records that create no work; slices
2–4 are then dead code behind no caller and can be reverted in the same
change. Storage changes are additive (handoff plan fields, `AgentJobState`/
`AgentJobInput` fields serialized inside the ledger JSON, TaskRun linkage and
receipt records, one event catalog type) — no destructive migration in either
direction.

## Open Questions

1. **Named-session continuation.** When two tasks in a run share
   `session: name`, does the second task continue the first invocation's
AgentSession (product-doc launch semantics: continuing a session creates a new
   SessionInput, not a second AgentJob), or does every work attempt mint an
   independent session under the frozen per-attempt lineage? The spec freezes
   per-attempt lineage; the docs promise continuation. Needs a decision before
   slice 5, and the answer must be reflected in `docs/agent-sessions.md`.
2. **Default deadline when `timeout` is omitted.** Adopt the runtime action
   default (60 min) as the per-invocation deadline, or leave the global 10-min
   backstop for handoffs that did not opt into a deadline? The design leans to
   the action default for parity with inline semantics.
3. **Read-surface placement.** Where exactly the invocation status and
   identifiers render in the Web UI task views (and whether the Agent detail
   view grows a "Workflow origin" section) is a UX decision deferred to the
   slice-5 implementation.
4. **`recovering` coverage.** Whether an AgentJob `Unknown` (report timeout)
   should surface as `executing` (current proposal: not terminal) or as
   `recovering` while reconciliation runs — settled by whichever mapping the
   read-surface specs exercise first.
