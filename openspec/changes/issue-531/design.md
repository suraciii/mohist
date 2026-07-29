## Context

The proposal identifies two scheduling models for the same execution plane. WorkflowRun already owns its active work and can rebuild a dispatch during polling. AgentJob instead prepares a dispatch, pushes it to `RunnerGrain`, and relies on Runner grain state plus `RunnerWorks` rows to redeliver, authorize task logs, and close out work. The AgentJob-to-Runner assignment and Runner-to-AgentJob reconciliation calls form a cycle and duplicate the work fact.

The `work-dispatch-ledger` spec requires each owner to be the sole durable source for its work, while the Runner remains the authority for presence, configured slots, and runner-loss closeout. Polling and reports remain the existing execution-plane transport; the change does not introduce a client-facing protocol or a new work owner.

## Goals / Non-Goals

**Goals:**

- Store AgentJob assignment, ready time, lifecycle, and dispatch snapshot in the AgentJob's durable state and a queryable AgentJob projection.
- Have `DispatchService` derive redelivery, assigned pending work, and new claims for both work owners from their ledgers on every poll.
- Keep the runner lifecycle gate as the single serialization point for concurrent polls, capacity checks, unregister, and closeout.
- Remove Runner-owned AgentJob dispatch persistence, push assignment, reconciliation, and report relaying.
- Preserve at-least-once reporting and the existing `Accepted` or `Stale` acknowledgement behavior.

**Non-Goals:**

- Changing `WorkDispatch`, poll, report, AgentJob launch, or AgentSession public contracts.
- Adding a generic cross-domain work aggregate, a queue service, a scheduler cursor, or a cache.
- Changing WorkflowRun task semantics, workflow stage locking, runner process retry behavior, or runtime execution.
- Recovering to the former Runner ledger after the new AgentJob ledger has accepted work.

## Decisions

### AgentJob owns a queryable dispatch projection

Extend `AgentJobState` with the fields required to reconstruct exactly one current dispatch: stable work id, assigned runner id, ready time, running time, and the AgentJob dispatch snapshot. Persist the same scheduling fields in the `AgentJobs` read projection with indexes for `Pending/Running` work by project, assigned runner, and readiness time. `AgentJobGrain.SaveAsync` updates this projection whenever its authoritative state changes; the projection is an owner read model, not another owner of the work.

The projection is queried to select candidates and build runner status/task-log views. Every selection is revalidated by the owning grain's claim or report command, so a stale read can only skip or lose a poll opportunity, never create a second execution or corrupt lifecycle state.

Alternative considered: retain `RunnerWorks` as the common query table. Rejected because it remains a second owner-facing work ledger and requires reconciliation after every owner transition. Alternative considered: enumerate AgentJob grains on every poll. Rejected because it cannot meet bounded query cost or ordering requirements.

### AgentJob admission records readiness but does not dispatch

After AgentJob launch preparation, admission selects an eligible runner as a capacity precheck and writes `AssignedRunnerId`, `ReadySince`, and the immutable dispatch snapshot in the AgentJob ledger. It does not call `IRunnerGrain` and does not transition the job to running. If every eligible runner is already at capacity, admission returns synchronous backpressure; an admitted job that remains pending because claim cannot complete is governed by its owner-controlled availability deadline.

`IAgentJobGrain.ClaimNextAsync(runnerId)` will atomically validate the assignment or eligibility, transition the pending job to running, and return the persisted dispatch. Its terminal report command continues to validate the same runner and work identity.

Alternative considered: omit admission and select a runner only during poll. Rejected because AgentJob launch must retain its existing immediate eligibility/backpressure decision. Alternative considered: treat admission as a capacity reservation. Rejected because no reservation can survive the interval before physical execution; only the poll-time claim can make the capacity decision.

### DispatchService owns stateless, mixed-owner poll assembly

`DispatchService` will use the existing poll admission gate, touch presence, and obtain the polling runner's project scope. It will assemble a response in this order:

1. Reconstruct all running WorkflowRun and AgentJob work assigned to the runner, then emit entries absent from `inFlight ∪ awaitingAck`.
2. Claim pending work already assigned to that runner.
3. Claim eligible unassigned work.

Within each pending layer, the service merges WorkflowRun and AgentJob candidates by `ReadySince` ascending. It calculates spare capacity from both owner projections and rechecks it at every claim while the runner lifecycle gate is held. A failed claim or an ordinary dispatch-rendering failure leaves the owner work eligible for the next poll. A deterministic retired-action failure is sent to the exact active owner work identity.

Alternative considered: separate AgentJob and Workflow polling loops. Rejected because they would independently consume slots and cannot provide a single recovery-first ordering. Alternative considered: persist a dispatch cursor or queue in `DispatchService`. Rejected because owner-led reconstruction already provides delivery recovery and avoids another ledger.

### Runner is reduced to execution resource lifecycle

Remove `AssignAgentJobAsync`, `ReconcileAgentJobsAsync`, `ReportAgentJobResultAsync`, Runner work state, `RunnerWorkStore`, and the `IAgentJobWorkCoordinator` relay. Keep registration data, slots, poll admission, presence timeout, and closeout in `RunnerGrain`.

The report route will dispatch AgentJob reports directly to `IAgentJobGrain`, matching the Workflow report path. `GetRuntimeStateAsync` and `TaskLogService` will read AgentJob's projection for work assigned to the runner, using the same active-work identity checks that WorkflowRun already uses. On unregister or presence expiry, closeout queries running work from both owner projections and reports `runner-lost` directly to each owner; AgentJob records a failed terminal result rather than `Unknown`.

Alternative considered: leave the Runner table as an audit record only. Rejected because task-log authorization and status views would still depend on a second work record. The existing owner result, task log, and domain event records remain the audit trail.

### Availability timeout is based on owner readiness

Replace AgentJob dispatch-attempt backoff, acceptance fences, and retry-bound failure with an owner reminder that evaluates `ReadySince`. A pending job whose ready duration exceeds the configured availability timeout becomes terminal with `runner-unavailable`; a running job keeps the existing execution timeout behavior. The reminder is not a dispatch loop.

Alternative considered: retain retry attempts as a second timeout input. Rejected because polling frequency and transient Runner grain failures would affect the business outcome without representing a work-state fact.

## Risks / Trade-offs

- [AgentJob projection lags grain state] -> All claims and reports revalidate in `AgentJobGrain`; the projection is refreshed on every state save and stale rows are treated as candidates, not authority.
- [Mixed-owner ordering requires multiple queries] -> Add narrow indexed queries for assigned running, assigned pending, and eligible pending work; assert query count does not grow with terminal history.
- [A release occurs while AgentJobs are active] -> Backfill scheduling projection fields from the persisted AgentJob state before enabling the new poll path; owner state contains the stable work and runner identities needed for reconstruction.
- [Runner-loss semantics change from unknown to failed] -> Update AgentJob, session close, failure event, and report replay specs together so a late report is stale rather than a recovery trigger.
- [Removing RunnerWorks breaks task-log uploads] -> Move AgentJob active-work validation to the AgentJob projection before removing the table and retain focused authorization coverage.
- [Rollback after new work starts cannot restore the old push ledger] -> Treat the behavioral cutover as forward-only; correct post-cutover faults with a forward migration while schema expansion remains reversible.

## Migration Plan

1. Add AgentJob projection columns and indexes, then backfill each active AgentJob's assignment, work id, readiness, and status from its persisted state JSON. Keep `RunnerWorks` unchanged during this schema-only deployment.
2. Deploy the owner-led AgentJob state transitions, projection queries, direct report route, and unified `DispatchService` polling. Verify active work, redelivery, capacity, task-log authorization, runner-loss, and availability-timeout specs with fake time and in-memory stores.
3. Remove Runner AgentJob assignment/reconciliation interfaces, Runner work state hydration, `RunnerWorkStore` consumers, and legacy tests that assert Runner-held AgentJob work. Replace them with owner-led dispatch and active-work tests.
4. After the new path has no `RunnerWorks` reads or writes, drop the `RunnerWorks` schema and its migration-only backfill support.

Rollback is supported only before step 2 handles new work. After cutover, keep the expanded AgentJob schema and apply forward fixes; reverting to a binary that expects Runner-held work would lose the new ledger's execution semantics.

## Open Questions

None. The availability timeout will reuse the existing AgentJob configuration value, measured from `ReadySince` rather than dispatch attempts.
