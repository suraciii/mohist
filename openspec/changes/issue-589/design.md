## Context

Workflow currently has one path for two different facts. A conclusive Runner result reaches `ReceiveTaskReportAsync` and settles the task, while AgentSession stop, turn, and activity observations reach `AbandonActiveWorkAsync` and also settle the task. `RunnerGrain.CloseoutLostAsync` has the same problem: it sends `FailActiveWorkAsync(..., "runner-lost")` for every assigned Workflow. Both paths eventually call `WorkflowRun.FailTask`, so an unconfirmed physical stop or lost Runner becomes `TaskFailed` without an authoritative Agent result.

The report path cannot repair this after the fact. `WorkflowReportService` and `WorkflowGrain.ReceiveTaskReportAsync` accept only `FindActiveWork(workId, workerId)`. Once an inconclusive observation removes or fails active work, a matching late result is stale. The Runner then retires its in-memory `awaitingAck` entry because both `Accepted` and `Stale` are successful acknowledgements.

Issue #562 already gives AgentSession a durable stop-operation identity, target, disposition, deadline, and recovery reminder. This change must consume those physical facts without taking ownership of stop delivery. WorkflowRun remains the only owner of task outcome.

The following existing boundaries constrain the design:

- `WorkflowRuns.State` is the authoritative aggregate record and is committed transactionally with Workflow events.
- One WorkflowRun executes at most one task or checks batch at a time.
- A task attempt already has a stable `TaskRun.Id`, `WorkId`, and assigned Runner.
- Workflow AgentSession names can be reused. The Session-level `work-id` label is mutable metadata and is not a safe identity for a delayed turn observation.
- Runner work reports currently carry `workflowRunId`, `workId`, and the authenticated route Runner, but no AgentSession or AgentTurn identity.
- `TaskReportStatus` has only `Succeeded` and `Failed`; an inconclusive execution fact is not a third authoritative result.

## Goals / Non-Goals

**Goals:**

- Keep one Workflow-owned settlement for each Workflow Agent task attempt from dispatch until one authoritative result wins.
- Freeze the AgentSession, AgentTurn, physical runtime target, Runner, task attempt, and work identity before Agent execution.
- Treat physical stop, activity, turn, transport, and Runner-connectivity facts as observations that cannot complete or fail a task.
- Reconcile duplicate observations and matching result reports idempotently against the original execution.
- Stop redelivery and replacement execution while the original result is unresolved.
- Move unresolved work to a visible, actionable `blocked` state at one fixed durable deadline.
- Accept a matching authoritative result before or after blocking and apply normal Workflow completion or failure semantics exactly once.
- Preserve existing conclusive failure behavior for non-Agent tasks, checks, dispatch validation, workspace preparation, and Agent results that explicitly establish failure.

**Non-Goals:**

- Redesigning AgentSession stop delivery, stop retry, or physical target reconciliation from issue #562.
- Recovering an Agent result by inferring it from idle, completed, missing, or stopped physical state.
- Redispatching the original Agent task or creating a replacement attempt to resolve uncertainty.
- Automatically reopening historical WorkflowRuns that already contain a false `TaskFailed`; their original execution identity is incomplete.
- Changing retry policy for unrelated failures.

## Decisions

### A. Persist settlement on the Workflow task attempt

Add one optional `AgentResultSettlement` to `TaskRun`. It is created when a Workflow Agent task is claimed and contains only current arbitration facts:

```text
AgentResultSettlement
  state = awaiting-result | unknown | blocked
  taskRunId
  workId
  runnerId
  agentSessionId?          # bound before execution
  agentTurnId?             # bound before execution
  runtime?
  runtimeSessionId?
  stopOperationId?
  reasonCode?
  message?
  firstUnknownAt?
  deadlineAt?
```

`mohist/agent`, `mohist/opencode`, and `mohist/pi` tasks use this settlement. Other task types retain their current state machine.

The first record is `awaiting-result`; it has no uncertainty deadline because ordinary execution is still in progress. The first inconclusive observation changes the same record to `unknown`, stamps `firstUnknownAt`, and stores `deadlineAt = firstUnknownAt + AgentResultSettlementTimeout`. Later observations may update the latest reason or fill previously unknown physical identity fields, but cannot replace the execution identity or deadline.

The settlement is cleared when an authoritative result wins. The terminal `TaskRun` retains `WorkId` and `WorkerId`, which are sufficient to recognize duplicate reports without retaining a second historical result record. Traceable settlement transitions remain in Workflow events and AgentSession history.

The settlement belongs inside `WorkflowRun` state rather than a new table. Task outcome, settlement arbitration, status, and emitted events then commit in one existing `WorkflowRunStore.SaveAsync(run, events)` transaction. A separate row would require a cross-write protocol between the settlement and task outcome and would allow both to win after a partial failure.

**Alternative considered: add only `TaskRunStatus.Unknown`.** Rejected because a status value cannot retain the execution fence, physical target, fixed deadline, or stop-operation identity needed for reconciliation.

**Alternative considered: store settlement in an independent table.** Rejected because it duplicates WorkflowRun arbitration and introduces a consistency boundary without enabling independent access that the current contract needs.

### B. Freeze the Session/Turn binding before Agent execution

The runtime-event HTTP request already carries `workId`, `workType`, and `stage`, but `RunnerRoutes` currently drops those fields before calling AgentSession. Preserve them in `AppendAgentSessionRuntimeEventsCommand`. When AgentSession accepts the turn-opening `session.input`, it freezes a `SessionWorkflowExecutionBinding` on that `AgentTurnRecord`:

```text
workflowRunId + taskRun/workId + runnerId
agentSessionId + agentTurnId
runtime + runtimeSessionId
```

AgentSession then calls a narrow Workflow port operation, `BindAgentExecutionAsync`, before acknowledging the turn-opening input. Workflow accepts an exact duplicate as a no-op and rejects a mismatch for the same `workId`. The Runner already waits for the durable input receipt before invoking the runtime, so a failed Workflow binding leaves the input in the Runner runtime-event outbox and prevents execution until replay succeeds.

Every later AgentSession observation is built from the frozen Turn binding, never from the Session's current `work-id` label. The observation may add the current stop-operation identity, but it cannot change the bound task, turn, Runner, or physical runtime target.

The call direction remains `AgentSession -> WorkflowRun`. WorkflowRun never calls AgentSession synchronously, so this does not create a grain-call cycle. AgentSession keeps physical stop recovery; Workflow only records the resulting observation.

**Alternative considered: match late reports using only current AgentSession labels.** Rejected because a reused named Session overwrites non-source labels, so a delayed close or stop could target a later task.

**Alternative considered: add all Session/Turn fields to every Runner work result.** Rejected as the primary fence because the Server already authenticates the Runner and owns the `workId` binding. Reports match the Workflow execution by `workId + runnerId`; Session/Turn fields are physical reconciliation facts supplied by AgentSession, not caller-selected task identity.

### C. Separate inconclusive observations from authoritative results

Replace `ISessionWorkPort.AbandonActiveWorkAsync` with two identity-fenced operations:

- `BindAgentExecutionAsync(binding)` records the physical binding for the current settlement.
- `ObserveAgentExecutionAsync(observation)` records idle, completed, failed, cancelled, unknown, stopped, stop-unconfirmed, target-missing, or disconnected physical facts without selecting a task result.

AgentSession sends an observation for every terminal or indeterminate Workflow Turn fact. A normal conclusive Runner report may arrive immediately afterward and settle the short-lived `unknown` record; ordering between the Session event channel and result channel is therefore harmless.

Runner work results gain an explicit `unknown` status for Agent execution whose result is not authoritative, including the existing `hasUnconfirmedCleanup(...)` case. `WorkflowItemTranslator` must not map this status to `TaskReportStatus.Failed`; it routes it to `ObserveAgentExecutionAsync` and acknowledges only after the unknown settlement is durable. `TaskReportStatus` remains the two authoritative values `Succeeded` and `Failed`.

`RunnerGrain.CloseoutLostAsync` also becomes type-sensitive. Running checks and non-Agent tasks keep the existing conclusive `runner-lost` failure. A Workflow Agent task receives a `runner-disconnected` observation against its existing settlement and does not emit `TaskFailed`.

Repeated observations are no-ops after identity validation. An observation for a terminal task, a superseded attempt, a different Runner, or a different Turn cannot reopen or overwrite the task.

### D. Use explicit unresolved and blocked state-machine transitions

The settlement state machine is:

```text
awaiting-result + authoritative success/failure -> normal terminal task transition
awaiting-result + inconclusive observation      -> unknown
unknown         + duplicate/new observation     -> unknown, same deadline
unknown         + deadline                      -> blocked
unknown         + authoritative result          -> normal terminal task transition
blocked         + authoritative result          -> normal terminal task transition
blocked         + observation                   -> blocked
terminal task   + any observation/report         -> no-op acknowledgement
```

Add internal `AwaitingAgentResult` and visible `Blocked` values to `TaskRunStatus`, `StageRunStatus`, and `WorkflowRunStatus`. On the first unknown observation, all three move to `AwaitingAgentResult`. Wire status remains `running` during this bounded reconciliation window, while the task's settlement view explicitly exposes `state: unknown`, its reason, and deadline. At expiry, all three move to wire status `blocked`.

`Blocked` is nonterminal and non-dispatchable. `WorkflowRunStatus.IsTerminal` remains true only for `Stopped` and `Completed`; assignment, workspace, task attempt, work identity, and profile binding remain intact. `FailureDetails` stays null because blocked is not a failure.

Dispatch and control predicates must treat both unresolved states explicitly:

- `ClaimNextAsync`, `NextWork`, pending-work projection, and scheduler candidate queries cannot dispatch another task.
- `WorkflowRunWorkProjection` retains the task map for late lookup but no longer publishes the unresolved task as desired active work, so an empty Runner report cannot cause redelivery after a process restart.
- A key still reported by a connected Runner as `inFlight` or `awaitingAck` continues to consume a Runner slot, even though the Server no longer desires redelivery for it.
- Retry, rerun, rerun-from-stage, and runtime task insertion reject with `agent_result_unresolved`; they cannot create replacement work to settle uncertainty.
- Explicit Workflow stop remains the operator escape hatch. It invalidates the unresolved settlement, makes later reports stale, and releases normal Workflow resources without writing `TaskFailed` for the missing Agent result.

Sequential stage locks remain held through `unknown` and `blocked`. Releasing a lock while the physical target may still be active would permit overlapping effects on the protected resource. A matching result releases the lock through the normal `StageCompleted` or `StageFailed` event path; explicit Workflow stop releases it through the existing stop path.

**Alternative considered: keep all domain statuses `Running` and derive blocked only in views.** Rejected because scheduler, redelivery, retry, lock, and control decisions would continue to treat the task as ordinary running work.

**Alternative considered: release the stage lock at the blocked deadline.** Rejected because the deadline bounds Workflow uncertainty; it does not prove that the physical target stopped.

### E. Arbitrate late reports before report side effects

Report lookup changes from `FindActiveWork(workId, workerId)` to an identity lookup over the persisted task attempts. A report is eligible when exactly one task has the supplied `workId`, its stored Runner matches the authenticated route Runner, and it is either ordinary active work or has a matching `unknown` / `blocked` settlement.

`WorkflowReportService` performs only pure envelope validation and translation before entering the grain. Artifact binding, follow-up task projection, output mutation, event creation, and task advancement occur only after the Workflow grain has selected the report as the winner. This prevents two concurrent duplicate or conflicting late reports from performing durable side effects before grain arbitration. Existing artifact upload/bind idempotency remains the retry boundary if a grain save fails after binding.

A matching result applies the existing completion or failure behavior to the original `TaskRun.Id` rather than `CurrentTask()`:

- success records output/artifacts, completes that task, clears settlement, restores the stage from `Blocked` or `AwaitingAgentResult`, and runs normal advancement;
- failure records output/error, fails that task, clears settlement, and emits the existing `TaskFailed`, `StageFailed`, and `WorkflowRunFailed` events;
- a duplicate matching the already terminal task returns the existing acknowledgement without events or side effects;
- a conflicting later result or stale physical observation is acknowledged stale and changes nothing.

The dispatch snapshot is deleted on the first unknown transition. Reconciliation preserves the minimal execution identity in WorkflowRun state and never redispatches the payload; retaining a potentially large snapshot through an indefinitely blocked state would violate the WorkflowRun storage boundary.

### F. Use one Workflow reminder for the persisted deadline

`WorkflowGrain` implements `IRemindable` with one `agent-result-settlement` reminder. At most one Workflow task can be unresolved, so the grain does not need per-task reminder names or a polling scanner.

The first unknown transition persists the absolute UTC deadline using the injected `TimeProvider`, then registers or updates the reminder for that deadline. Redelivery always reads the stored deadline and cannot extend it. On activation, the grain repairs reminder registration from the persisted settlement. A reminder tick before the deadline re-registers for the remaining duration; a due tick atomically writes `Blocked` statuses and blocked events, then unregisters the reminder. A result or explicit stop also unregisters it after the state commit.

The initial `AgentResultSettlementTimeout` is five minutes, matching the existing AgentSession stop-recovery bound. It is Server configuration, but each settlement stores its computed absolute deadline so later configuration changes do not alter in-flight work. Tests inject `TimeProvider` and use Orleans reminder entry points; they do not wait on wall clock or poll.

The reminder never queries AgentSession and never issues a stop. AgentSession's issue #562 reminder continues to reconcile the exact physical target and repeats the same stop operation only when that target is authoritatively still active.

### G. Add blocked events and actionable read projection

Add Workflow events for the two observable settlement milestones:

- `AgentTaskResultUnconfirmed(stage, taskId, workId, reason, deadlineAt)` records the first unknown transition.
- `TaskBlocked`, `StageBlocked`, and `WorkflowRunBlocked` record expiry without reusing any failure event.

The blocked events enter `WorkflowEventSerializer`, `EventCatalog`, lineage, event query, and Web canonical-event handling. Failure-only subscribers must not consume them as `WorkflowRunFailed`. Issue, Inbox, notification, and GitHub projections may present blocked attention, but cannot offer failed-task retry or infer `FailureReason.TaskFailed`.

Add an `AgentResultSettlementView` to the task status view with `state`, stable reason code, message, deadline, and next action. A blocked run also projects the same actionable details at the top level so CLI and Web status headers do not need to search task history. The stable blocked reason is `agent-result-unconfirmed`; its next action tells the operator to restore the original Runner and allow result replay, inspect the bound AgentSession/Turn, or explicitly stop the run after confirming the physical target is no longer active.

`WorkflowStatusMapper` maps all three blocked enums to `blocked`. CLI and Web status unions, pills, stage icons, task rows, logs, and issue projections add a blocked presentation. Blocked is not added to CLI terminal-run polling because a late authoritative result can still settle it. Run and stage `Failure` fields remain null; clients read the settlement reason rather than inventing a failure from AgentSession activity.

## Risks / Trade-offs

- [Every Workflow Agent turn may briefly enter unknown because Session close can arrive before its result report] -> The transition is idempotent and normally short-lived; it is the honest state between the two independently delivered facts. Only the fixed deadline becomes user-blocking.
- [A Session binding replay could target a later task through mutable labels] -> Freeze Workflow work identity on the AgentTurn at input acceptance and build every observation from that record.
- [A stale Runner could submit a late result after assignment or status changed] -> Match the authenticated Runner, persisted work id, task attempt, and settlement; reject any identity mismatch before side effects.
- [Artifact binding occurs outside the WorkflowRun database transaction] -> Run it only after serialized grain arbitration and retain its existing idempotent upload identity so a failed Workflow save can replay without duplicate artifacts.
- [Reminder registration is not in the WorkflowRun transaction] -> Persist the absolute deadline as authority, surface registration failure to the observation caller, and repair registration on grain activation. Reminder replay rechecks state and time before changing status.
- [Blocked work can hold a sequential stage lock indefinitely] -> This is intentional while physical effects are unconfirmed. The actionable recovery path is result reconciliation or explicit Workflow stop after the operator verifies the target.
- [Removing unresolved work from desired dispatch could free Runner capacity too early] -> Count the Runner's reported `inFlight` and `awaitingAck` keys for capacity while connected, but never use them as authority to redispatch the Workflow task.
- [New enum values break exhaustive consumers] -> Update Server wire mappers, event serializers, CLI, Web unions, and exhaustive tests in the same coordinated release.
- [A previous Server cannot deserialize or interpret new blocked state/events] -> This project does not support rolling version compatibility; deploy Server, Runner, CLI, and Web together and use the migration/rollback procedure below.

## Migration Plan

1. Add settlement models, state-machine transitions, reminder handling, blocked events, and domain tests. Split active-work lookup from reportable-settlement lookup and split desired-dispatch counting from reported Runner capacity.
2. Preserve runtime-event work metadata, freeze AgentTurn bindings, replace AgentSession abandonment with observation commands, and make Runner loss Agent-aware. Add replay, stop-operation, Session-reuse, and Runner-reconnect tests.
3. Emit Runner `unknown` results for inconclusive Agent outcomes, route them outside `TaskReportStatus`, and move report side effects behind Workflow arbitration. Add before/after-deadline, duplicate, conflicting-result, artifact, and process-restart tests.
4. Add blocked API/event projections and update Issue, CLI, Web, notification, Inbox, and GitHub consumers. Pin that blocked is visible, actionable, nonterminal, and never represented by failure fields or events.
5. Before deployment, drain or explicitly stop running Workflow Agent tasks so every new execution obtains a frozen Turn binding. No EF schema migration is required because settlement lives in WorkflowRun JSON state; no historical failed run is reopened automatically.
6. Run `npm run test:fast`, then the full `npm run verify` gate with fake time and fake external dependencies.

**Rollback:** take the normal consistent SQLite backup before deployment. Once any run writes the new enum values or blocked event types, rollback to an older binary requires restoring that pre-deployment backup; no live reverse conversion is provided. Before the first new-state write, the release can be reverted normally.

## Open Questions

- Is five minutes the desired production default for `AgentResultSettlementTimeout`, or should operations choose a longer reconnect window? The persisted absolute-deadline model and tests do not depend on the default.
- Should `WorkflowRunBlocked` trigger an immediate user notification in every channel, or only update Issue/Inbox/Web attention until the existing notification policy is extended? It must be available on the event bus either way.
