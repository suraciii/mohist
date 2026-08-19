## Context

Issue 627 addresses a liveness gap in Workflow-owned Agent execution. The current flow records an unresolved Agent result as `Unknown`, schedules a durable deadline reminder, and retains the running task and Runner assignment so recovery can be attempted. At the deadline, `WorkflowRun` changes the settlement to `Blocked` and emits blocked events, but the run can still retain its assignment, dispatch-related state, and stage lock. The Runner therefore continues to count the run as active even though the physical Agent execution is no longer being redelivered.

The Workflow aggregate owns the authoritative attempt and its `AgentResultSettlement`. `TaskRun.WorkId`, `TaskRun.WorkerId`, and settlement fields preserve the attempt identity. `WorkflowRun.Assignment` is operational ownership used by `CurrentActiveWorkFor`, the workflow work projection, Runner active-work discovery, and Runner slot accounting. Dispatch snapshots, Orleans reminders, and sequential stage locks are separate resources and must be cleaned up outside the aggregate.

The main stakeholders are the Workflow grain and run store, Runner polling and capacity accounting, AgentSession/Runner result ingress, and Issue, Inbox, event, and status projections. The implementation must preserve the existing nonterminal blocked attention model, work with Orleans grain activation and reminder replay, and retain enough identity for a late authoritative result without allowing the released attempt to become active again.

## Goals / Non-Goals

**Goals:**

- Make the persisted deadline an exactly-once boundary that changes `Unknown` to `Blocked` without changing task, stage, or WorkflowRun execution into success or failure.
- Release the active Workflow assignment and all Runner-visible active-slot ownership as part of the same durable run save as the blocked transition.
- Retain `WorkflowRunId`, `TaskRunId`, `WorkId`, `RunnerId`, AgentSession, AgentTurn, runtime, runtime-session, stop-operation, observation, reason, message, and deadline facts.
- Make snapshot deletion, reminder removal, and stage/resource lock release repeatable after partial failure or grain reactivation.
- Keep blocked attention visible and actionable while excluding the attempt from claims, redelivery, active-work views, and Runner capacity.
- Accept a matching late authoritative result exactly once through the existing full identity fence, without restoring assignment or capacity.
- Add fake-time, replay, failure-injection, Runner-capacity, projection, and late-result coverage.

**Non-Goals:**

- Inferring success, failure, cancellation, or physical stop from an AgentSession, AgentTurn, runtime, Runner, idle, or disconnected observation.
- Replaying the old AgentTurn, redelivering the old dispatch after release, auto-retrying the task, or creating a replacement `TaskRun` or `WorkId`.
- Stopping or deleting the physical AgentSession as part of deadline reconciliation.
- Redesigning Runner slot policy or introducing a new external dependency.
- Changing the semantics of an explicit operator stop; it remains a separate cancellation boundary with its existing stale-receipt behavior.

## Decisions

### 1. Use `Blocked` plus assignment removal as the durable release boundary

Extend the domain operation currently represented by `BlockUnresolvedAgentResult` so that, when the persisted deadline has passed, it atomically:

1. verifies the settlement is still `Unknown` and due;
2. changes it to `Blocked`;
3. emits the existing `TaskBlocked`, `StageBlocked`, and `WorkflowRunBlocked` events once; and
4. clears `WorkflowRun.Assignment` without clearing the task's `WorkId` or `WorkerId`.

The run status and task status remain `Running`; blocked status remains a projection of the settlement rather than a new terminal lifecycle state. The grain also clears its in-memory assigned-worker cache after the save so `GetAssignedWorkerIdAsync` cannot expose a stale owner during the current activation.

The assignment is an active-work lease, not the authoritative execution identity. The settlement and task fields are the identity record used for late reports. Clearing the assignment lets the existing `CountRunningAssignedToAsync` and `FindRunningAssignedToAsync` boundaries stop counting and discovering the released run without adding a second capacity flag.

An alternative is to retain the assignment and add an `ActiveOwnershipReleased` field to the run projection and every Runner query. That would preserve more routing behavior, but would duplicate ownership semantics across the serialized run, SQL projection, Runner grain, and read models. It is rejected because it increases the chance that one active-work path continues to hold a slot.

### 2. Reconcile cleanup after the durable boundary, one idempotent operation at a time

`ReconcileAgentResultSettlementAsync` will commit the blocked-and-unassigned run before attempting external cleanup. Once the settlement is already `Blocked`, later reconciliation will skip event creation and only retry cleanup. Cleanup consists of:

- deleting the workflow dispatch snapshot for the original `WorkId`;
- releasing the stage/resource lock for the recorded stage; and
- unregistering the Agent-result settlement reminder.

Each operation will be safe when the resource is already absent or released. Reconciliation will attempt independent cleanup steps and log failures so one failed operation does not undo the durable boundary. A later reminder replay, grain activation, or explicit reconciliation retries all steps. No cleanup retry may call claim, dispatch, assignment, or result-settlement code.

The reconciler will also repair older `Blocked` records that still have an assignment: it will clear the assignment and persist that repair before retrying external cleanup. This makes partial cleanup and deployment of the fix convergent without a new cleanup state machine.

### 3. Define active work from active ownership, not from a running task alone

Update the active-work boundaries to require the run's current assignment and worker match. In particular:

- `CurrentActiveWorkFor`, `GetCurrentWorkIdAsync`, and `WorkflowReadModel.GetActiveWork` return no active work after assignment release.
- `WorkflowRunWorkProjectionBuilder` persists null active-work and active-worker values for a released attempt.
- `DispatchService` treats a blocked settlement as non-recoverable for polling and returns no dispatch with no slot reservation, including when it observes a stale pre-release snapshot or a run during reconciliation.
- `HasUnresolvedAgentResult` and `HasDispatchableWork` continue to prevent replacement claims. A blocked run cannot become claimable merely because its assignment is gone.
- Runner status and used-slot views are derived only from the active-work set, so another eligible work item can use the released slot.

The existing `AttentionStatus = blocked` projection remains the consumer index for blocked attention. During rollout, Runner database queries will also exclude rows with blocked attention, which protects capacity for pre-existing blocked records before their grains have activated and repaired their assignments.

### 4. Use an additive Runner report envelope for late authoritative results

The authoritative terminal-result ingress is the existing `POST /api/runner/{runnerId}/report` workflow route. Extend `RunnerReportRequest` with nullable, additive `AgentSessionId`, `AgentTurnId`, `Runtime`, and `RuntimeSessionId` fields. For a Workflow Agent task, all four fields are required for acceptance; the route returns the existing stale acknowledgement for an absent or incomplete binding. The URL runner id and existing `WorkflowRunId`, `TaskRunId`, and `WorkId` fields provide the other identity components, and the Workflow grain route supplies the WorkflowRun identity. The result body is never accepted by matching only the reusable task/work/Runner tuple.

The workflow Agent dispatch/session path supplies the four fields from the immutable execution binding for the turn. Additive workflow-dispatch fields named `AgentSessionId`, `AgentTurnId`, `Runtime`, and `RuntimeSessionId` carry the logical session/turn and, once attached, the runtime binding; a fresh dispatch may have only the logical fields until the runtime attach/create acknowledgement returns. The Runner must submit the resulting complete binding through the existing `BindAgentExecutionAsync` path before marking a result ready, then carries the same values with the dispatch execution state and persists them alongside the result in its durable awaiting-ack journal before sending a terminal report. A retry therefore resends the identity captured for that turn, even if the current AgentSession has since been rebound. Missing binding at execution time is reported as an incomplete/stale Agent result rather than guessed from mutable session labels.

`WorkflowReportService` routes a Workflow Agent terminal report to a dedicated full-fence grain operation. That operation first reconciles a due settlement, then requires the complete tuple of WorkflowRun, task, work, Runner, AgentSession, AgentTurn, runtime, and runtime-session identity. It uses the centralized `MatchesAttempt`, `MatchesBoundFields`, and full-binding predicates shared with physical observation. A matching report may apply the normal task completion/failure semantics to the original Running task even when the settlement is already Blocked and `Assignment` is null, but it commits only the original task outcome: it never restores `Assignment`, stores a dispatch snapshot, reacquires a stage lock, or reserves a Runner slot. Once the task is terminal, duplicate receipts are stale and side-effect free. Physical observations after blocking remain stale and cannot change reason, deadline, or state.

The existing `ReceiveTaskReportAsync` and `ReceiveCheckReportAsync` paths remain unchanged for non-Agent work. Agent reports that do not carry all four binding fields, or that fail any WorkflowRun/task/work/Runner/AgentSession/AgentTurn/runtime/runtime-session comparison, receive stale acknowledgement before artifact binding, output mutation, event emission, or workflow advancement.

### 5. Preserve blocked projections while separating category from original reason

The blocked events retain their stable blocked category for existing Issue and Inbox consumers, and are emitted only by the first boundary transition. The settlement remains the source of truth for the original reason code, message, last physical observation, first-unknown time, deadline, stop-operation identity, and execution binding.

Status and task settlement views will expose the persisted reason/detail and deadline, with `agent-result-unconfirmed` as a fallback category when no original reason exists. Replaying the same blocked event or reactivating the grain must produce the same blocked attention and must not emit failure notifications, completion notifications, or duplicate blocked transitions.

### 6. Test the boundary as a state-and-projection protocol

Add or extend tests around the domain, Workflow grain, run-store projection, Runner grain, and dispatch service:

- Advance a controllable clock to exactly the persisted deadline and verify one blocked-and-unassigned save, unchanged identity fields, no success/failure events, and an unchanged second reconciliation.
- Inject failure into snapshot deletion, reminder removal, and stage-lock release independently and verify later reconciliation converges without duplicate events or renewed ownership.
- Deactivate/reactivate the Workflow grain and verify the settlement and identity survive while active-work and assigned-worker views remain empty.
- Poll a capacity-full Runner after release and verify the old attempt is absent, no recovery dispatch is emitted, no slot is reserved, and a different work item can claim capacity.
- Run a fake-time/failure-injection case with two concurrent unknown attempts on the same capacity-limited Runner. At the persisted deadline, verify both blocked transitions clear both active assignments at their durable boundaries, preserve both original identities, and leave both rows absent from Runner active-work and used-slot projections even when cleanup retries fail. Then verify another eligible work item claims capacity and matching late receipts for either original attempt remain fenced and never reoccupy a slot.
- Deliver matching late success and failure reports through the additive Runner report binding, then duplicate, incomplete, and mismatched bindings, and verify exactly one original-task outcome with stale, side-effect-free acknowledgements for the rest. Verify that the existing non-Agent report path is unchanged.
- Verify blocked status, Issue/Inbox attention, event projections, and Runner status preserve the reason and deadline without presenting the attempt as failed or running.

### 7. Keep one pending terminal task-log snapshot per work identity

Bootstrap verification exposed a Runner failure mode: a recovery execution can
reach the same `ownerKind:ownerId:workId` while the first terminal task-log
snapshot is still pending, but with a different captured batch. Treating that
second batch as a fatal local conflict prevents the Agent result from reaching
the durable report path and turns a duplicate execution into
`agent-result-unconfirmed`.

The terminal task-log outbox therefore keeps the first durable pending snapshot
as the authoritative snapshot for that work identity and returns it to the
caller when a later pending write differs. The later execution still reports
its result through the normal result/receipt path; terminal log delivery keeps
retrying the already persisted snapshot. Failed records retain their existing
conflict handling, including replacement only for the explicit
`terminal_snapshot_conflict` next-execution case.

An alternative would be to add an execution-attempt component to the terminal
log identity and make the server accept multiple snapshots per work. That
would expand the idempotency key, API behavior, cleanup, and tests without
being required to preserve the authoritative task result. It is rejected in
favor of the existing work identity boundary.

## Risks / Trade-offs

- [Cleanup fails after the blocked save] -> The active lease is already removed durably; every external cleanup step is idempotent, retried on reminder replay/activation, and logged for repair.
- [A pre-existing blocked row still has an assignment during rollout] -> Runner queries exclude indexed blocked attention immediately, and grain reconciliation clears the persisted assignment on the next activation.
- [Clearing assignment removes the normal Runner route for workspace operations] -> Preserve the original RunnerId and full execution binding in the settlement and keep workspace identity on the run; verify workspace cleanup behavior in rollout tests before enabling the repair sweep.
- [A stale report races the deadline] -> Serialized Workflow grain turns reconcile due deadlines before applying observations or reports; only a complete matching binding can win after release.
- [A legacy Runner lacks the additive Agent report fields] -> Treat its Workflow Agent terminal receipt as stale and do not release the full-fence requirement; deploy the server route and Runner journal/report changes together before enabling deadline release for those attempts. Non-Agent reports remain compatible with the existing envelope.
- [A running task is mistaken for active work by an unupdated consumer] -> Centralize active ownership on assignment plus the persisted active-work projection and add Runner, artifact, and read-model assertions for released attempts.

## Migration Plan

1. Deploy the domain and grain changes with nullable/additive serialized fields only; no database schema migration is required for the settlement record.
2. Deploy Runner and read-model query changes that exclude `AttentionStatus = blocked` from active capacity/redelivery and return no active work for a released assignment. This is backward-compatible with existing persisted runs.
3. Allow the Workflow activation reconciler to repair already-blocked runs by clearing their assignments and retrying snapshot, reminder, and stage-lock cleanup. Run a bounded repair sweep over indexed blocked runs if operationally necessary.
4. Observe Runner used-slot counts, blocked attention counts, cleanup retry logs, stale acknowledgements, and dispatch snapshot orphan counts. Confirm that late matching results still settle the original task.
5. Rollback consists of reverting application binaries while retaining the persisted `Blocked` state and cleared assignments. Do not restore assignments during rollback; an older binary will still treat the run as non-dispatchable, and the next forward deployment can resume cleanup repair. Any additive report fields are ignored by older callers.

## Open Questions

- Should the public blocked attention `Reason` be the persisted physical reason code, the stable `agent-result-unconfirmed` category, or expose both as separate category/detail fields? The specification requires the persisted reason to remain observable, while existing consumers may depend on the stable category.
- Does any workspace cleanup or workspace-read path require the original assigned Runner rather than an eligible Runner fallback after assignment release? If so, add a non-capacity routing fact separate from `WorkflowRun.Assignment`; it must never participate in slot accounting.
- Is the existing startup snapshot orphan sweep sufficient for cleanup failures, or should blocked-settlement reconciliation be exposed as an explicit maintenance operation with metrics and retry counts?
